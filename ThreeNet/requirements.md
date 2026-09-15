Name: Three.Net

Deskripsi:
Library 3D native multiplatform (inspirasi dari Three.js, tapi berbasis Rust + .NET), lalu saya susun roadmap pengembangan tahap demi tahap.

---

 📦 List of Features (Setara & Lebih dari Three.js)

# 🎨 Rendering Engine
- Abstraksi GPU lintas platform (Vulkan, Metal, DirectX, OpenGL ES).
- Deferred & forward rendering pipeline.
- HDR, PBR (Physically Based Rendering).
- Post-processing (bloom, SSAO, depth of field, motion blur).

# 🧩 Scene Graph
- Hierarki objek 3D (node, transform, parenting).
- Kamera (perspective, orthographic).
- Lights (directional, point, spot, area).
- Animasi transformasi & keyframe.

# 🖼️ Materials & Shaders
- Shader modular (GLSL/HLSL/WGSL).
- Material library: basic, lambert, phong, PBR.
- Custom shader injection.

# 📂 Asset Pipeline
- Loader GLTF/GLB, OBJ, FBX.
- Texture loader (PNG, JPEG, HDR).
- Model compression & streaming.

# 🕹️ Interactivity
- Input handling (keyboard, mouse, touch, gamepad).
- Raycasting untuk picking objek.
- Event system (onClick, onHover, drag).

# 🌍 Multiplatform Runtime
- Native desktop (Windows, Linux, macOS).
- Mobile (Android/iOS).
- Web (via WASM).
- Konsistensi API di semua platform.

# 🔊 Extensions
- Physics integration (Bullet/PhysX).
- Audio spatial 3D.
- VR/AR support (OpenXR).
- Networking (multiplayer sync).

---

 🛠️ Roadmap Pengembangan

# Fase 1 – Core Foundation
- Setup Rust core engine dengan `wgpu`.
- Basic rendering (mesh, camera, light).
- Scene graph minimal.
- Binding ke .NET (interop layer).

# Fase 2 – Rendering & Materials
- Tambah PBR & shader modular.
- Post-processing pipeline.
- Texture & material system.

# Fase 3 – Asset Pipeline
- Implementasi loader GLTF/OBJ.
- Texture streaming & compression.
- Asset manager untuk caching.

# Fase 4 – Interactivity
- Input system (keyboard, mouse, touch).
- Raycasting & event system.
- Basic UI overlay.

# Fase 5 – Multiplatform Runtime
- Compile ke desktop (Windows/Linux/macOS).
- Port ke mobile (Android/iOS).
- WASM build untuk web.

# Fase 6 – Extensions
- Physics engine integration.
- Audio spatial.
- VR/AR support via OpenXR.
- Networking untuk multiplayer.

# Fase 7 – Ecosystem & Tooling
- Editor/inspector (scene editor).
- Plugin system.
- Dokumentasi & SDK release.

---
 🌐 Sample Apps

- ThreeGallery → aplikasi code gallery berisi berbagai use case berbagai contoh penggunaan fitur library + tampilkan sample code-nya. Buat UI UX yang user friendly dan keren, dibuat dengan Avalonia UI (multi-platform)

---

 🌐 Tools

- Tools berupa aplikasi dengan Avalonia UI Bernama Three.Net App Generator (ThreeAppGen) bentuknya seperti code editor yang memiliki fungsi generate app with prompt dengan bantuan LLM menggunakan library semantic kernel, LLM yang disupport: OpenAI, Claude, Gemini, Ollama, settingnya (model, api key, endpoint, temperature, system prompt) disimpan di app.config. 
- Nama AI Assistant: Jack - The Code Bender
- Buatkan kernel functions yang diperlukan agar assisten AI-nya bisa membuatkan aplikasi dengan benar baik UI dan Backend Code-nya, kasih common functions juga untuk SearchInternet (tavily), ScrapeWebPage, MathCalculation, Check Date and Time, dan fungsi lain yang diperlukan. 
- Panel chat ada di sebelah kanan code editor, bisa attach gambar, bisa di resize width-nya dan hide/show, send chat bisa dengan Ctrl+Enter atau klik button send, ada button untuk clear chat thread, Model LLM bisa dipilih dibagian atas Chat Panel 
- Di tengah ada code editor, lengkap dengan line number, code highlight
- Di panel kiri ada code explorer seperti VSCode
- Pada menu dan toolbar terdapat fungsi: New Project (Folder), Open Project/File, Close Project, Go To Line Number, Format Code, Build, Run, Deploy, Exit. 
- Create new project ada 2 pilihan: Blank dan From Template (buatkan berbagai template jenis aplikasi untuk 3D Grafik, Animasi, Game, Simulator, dsb dengan use case bermacam-macam). 
- Terdapat status bar dan logs panel di bagian bawah untuk memantau proses dan output. 
- Show/hide line number pada code editor. 
- Buatkan dengan UI dan UX modern dengan skill frontend-design. Semua konfigurasi disimpan di app.config dan bisa di ubah di UI. 

---

 📌 Kesimpulan
Dengan roadmap ini, library akan berkembang dari core rendering engine hingga menjadi ekosistem lengkap setara Three.js, tapi dengan keunggulan native performance, memory safety, dan multiplatform support.  

Notes:
- gunakan .NET 10
- optimasi koding agar dapat performa terbaik dan memory efisien
- gunakan naming convention standard c#
- readme dalam bahasan Indonesia dan English
- dokumentasi lengkap di folder docs
- Progress.md untuk tracking development, PLAN.md untuk roadmap pengembangan
- jika ada hal-hal yang penting perlu ditambahkan, silakan ditambahkan langsung biar lengkap.
- di aplikasi dan dokumentasi tambahkan informasi dibuat oleh Gravicode Studios dipimpin Kang Fadhil
- untuk publish nuget, api key ada di 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt'
- ujicoba dengan LLM real bisa gunakan api dari 'C:\Users\mifma\Documents\CodeSandbox\testkey.txt'