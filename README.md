# INTI Virtual Hub – ASP.NET Core + Firebase Dashboard

### 🧩 Source Code
For the **Unity WebGL front-end**, please visit:  
🔗 [INTI Virtual Tour (GitHub Pages Build)](https://github.com/Chong-Kai-Juan03/Unity-INTI-Virtual-Tour-Webgl-Hosting-Files)

---

### 🎯 Overview
This repository contains the **admin web application and backend system** for the INTI Virtual Tour project.  
It is built using **ASP.NET Core MVC** and integrates directly with **Firebase Realtime Database** and **Cloud Storage**.

The system enables administrators to:
- Upload and manage virtual tour scenes (360° images, descriptions, and locations).  
- Connect and synchronize data between Unity WebGL builds and Firebase.  
- Monitor scene statistics such as visits, interactions, and user engagement.  

This dashboard works together with the Unity WebGL project linked above.

---

### ⚙️ Features
✅ **Firebase Integration** – Reads and writes scene data, upload links, and metadata.  
✅ **Cloud Storage Uploads** – Upload 360° images directly to AWS S3 Bucket.  
✅ **User Authentication** – Simple session-based login for admin and staff.  
✅ **Dynamic Scene Management** – Add, edit, delete, and update tour scenes in real time.  
✅ **Statistics Dashboard** – Visualize scene view counts, uploads, and user activity.  
✅ **ASP.NET MVC Architecture** – Organized in `Controllers`, `Models`, `Views`, and `Services`.  
✅ **Docker Support** – Includes Dockerfile for containerized deployment.  

---

### 🧰 Technology Stack

| Category | Technology Used |
|-----------|-----------------|
| **Framework** | ASP.NET Core MVC (.NET 6/7) |
| **Frontend** | Razor Pages, Bootstrap, JavaScript |
| **Backend** | C#, Firebase Realtime Database |
| **Hosting** | Azure App Service / Docker |
| **Storage** | AWS S3 Bucket |
| **Version Control** | Git & GitHub |

---

### 🧱 Project Structure

| Folder / File | Description |
|----------------|-------------|
| **Controllers/** | Handles routing and logic for pages (e.g., `HomeController`, `FirebaseController`). |
| **Models/** | Defines data models such as `Scene`, `User`, and `Statistics`. |
| **Views/** | Contains Razor pages for the admin UI and management tools. |
| **Services/** | Includes connectors for Firebase and Cloudinary. |
| **wwwroot/** | Static web assets like JS, CSS, and image files. |
| **appsettings.json** | Stores Firebase configuration and environment variables. |
| **Dockerfile** | Used for containerized deployment via Docker. |
| **Program.cs** | Entry point for the ASP.NET Core application. |

---

### 🚀 Getting Started

#### 1️⃣ Prerequisites
Ensure you have installed:
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with .NET SDK  
- [.NET 6 or later](https://dotnet.microsoft.com/en-us/download)  
- [Node.js](https://nodejs.org/) for npm dependencies  
- Firebase project credentials  

---



