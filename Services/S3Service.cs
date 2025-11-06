// File: Services/S3Service.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace firstconnectfirebase.Services
{
    public class S3Service
    {
        private readonly IAmazonS3 _s3;
        private readonly ILogger<S3Service> _logger;
        private readonly string _bucket;
        private readonly string _publicBaseUrl; // e.g. https://<bucket>.s3.ap-southeast-1.amazonaws.com

        public S3Service(IAmazonS3 s3, IConfiguration config, ILogger<S3Service> logger)
        {
            _s3 = s3;
            _logger = logger;

            // Support both ":" and "__" keys
            _bucket = config["AWS:BucketName"] ?? config["AWS__BucketName"]
                ?? throw new InvalidOperationException("Missing AWS:BucketName");

            // Prefer explicit, else derive from region (avoids extra redirect)
            var region = config["AWS:Region"] ?? config["AWS__Region"];
            _publicBaseUrl = config["AWS:PublicBaseUrl"] ?? config["AWS__PublicBaseUrl"]
                ?? BuildRegionalBaseUrl(_bucket, region, _s3.Config?.RegionEndpoint?.SystemName);
        }

        private static string BuildRegionalBaseUrl(string bucket, string? regionFromConfig, string? regionFromClient)
        {
            var region = regionFromConfig ?? regionFromClient;
            // Default global endpoint if region is unknown
            return string.IsNullOrWhiteSpace(region)
                ? $"https://{bucket}.s3.amazonaws.com"
                : $"https://{bucket}.s3.{region}.amazonaws.com";
        }

        /* ----------------------------- Upload ----------------------------- */

        public async Task<string> UploadFileAsync(IFormFile file, string s3Key, bool makePublic = true, string? cacheControl = null)
        {
            if (file is null || file.Length == 0) throw new ArgumentException("Empty file.", nameof(file));
            if (string.IsNullOrWhiteSpace(s3Key)) throw new ArgumentException("Missing key.", nameof(s3Key));

            using var stream = file.OpenReadStream();
            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? GuessContentType(Path.GetExtension(file.FileName))
                : file.ContentType;

            var req = new TransferUtilityUploadRequest
            {
                BucketName = _bucket,
                Key = s3Key,
                InputStream = stream,
                ContentType = contentType
            };

            if (makePublic) req.CannedACL = S3CannedACL.PublicRead;
            if (!string.IsNullOrWhiteSpace(cacheControl))
                req.Headers.CacheControl = cacheControl;

            var util = new TransferUtility(_s3);
            await util.UploadAsync(req);

            return $"{_publicBaseUrl}/{s3Key}";
        }

        /* ----------------------------- Delete ----------------------------- */

        public async Task DeleteFileAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            try
            {
                await _s3.DeleteObjectAsync(_bucket, key);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Key already gone – treat as success
                _logger.LogInformation("Delete ignored; key not found: {Key}", key);
            }
        }

        /* --------------------------- Presigned URLs ----------------------- */

        public string GetPresignedGetUrl(string key, TimeSpan ttl)
        {
            var req = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(ttl)
            };
            return _s3.GetPreSignedURL(req);
        }

        public string GetPresignedGetUrlWithAttachment(string key, string fileName, TimeSpan ttl)
        {
            var req = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(ttl),
                ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentDisposition = $"attachment; filename=\"{fileName}\""
                }
            };
            return _s3.GetPreSignedURL(req);
        }

        /* ----------------------------- Utilities -------------------------- */

        public static bool TryParseKeyFromUrl(string? url, out string key)
        {
            key = "";
            if (string.IsNullOrWhiteSpace(url)) return false;

            try
            {
                var u = new Uri(url);
                // Works for both virtual-hosted-style and path-style endpoints
                key = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
                // Strip trailing query if any (AbsolutePath already excludes query)
                return !string.IsNullOrWhiteSpace(key);
            }
            catch { return false; }
        }

        private static string GuessContentType(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            // Minimal map; extend as needed
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".tiff" => "image/tiff",
                ".svg" => "image/svg+xml",
                ".json" => "application/json",
                ".txt" => "text/plain",
                ".html" => "text/html",
                _ => "application/octet-stream"
            };
        }
    }
}
