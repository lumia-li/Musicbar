# 🎵 MusicBar

**English** | [简体中文](README.md)

> A global music control widget that snaps to the Windows taskbar, letting you easily control the music currently playing without ever switching away from the window you are in.

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Framework](https://img.shields.io/badge/.NET-8.0-512BD4)
![Language](https://img.shields.io/badge/language-C%23/WPF-178600)
![License](https://img.shields.io/badge/license-MIT-blue)

---

## ✨ About

**MusicBar** is a Windows desktop music control widget built with **C# / WPF (.NET 8)**. It talks to Windows' built-in multimedia session through the system-level **GSMTC (Global System Media Transport Controls)** interface, so it can uniformly control mainstream music players without requiring any player-specific plugin or API.

It lives at the edge of your screen / beside the taskbar like a "mini music control bar". Whether you are coding, gaming, or watching videos, you can always see the current song and perform **play / pause, previous / next** with one click — global control that stays elegantly out of your way.

---

## 🎯 Core Features

### 🎤 Supported Players
- **NetEase Cloud Music** (NeteaseCloudMusic)
- **KuGou Music** (KuGouMusic)
- **Soda Music** (SodaMusic)
- **MoeKoe Music**
- **QQ Music**
- **Spotify** (untested)
- **YouTube Music**

### 🎨 Visuals & Interaction
- **Dock to Taskbar**: drag and release near the taskbar to snap automatically.
- **Dark / Light Themes**: auto-follows the system theme, or switch manually.
- **Frosted Glass Effect**: shows an acrylic texture when hovering.
- **Spectrum Visualization**: a built-in spectrum dances along with the music.
- **Three Display Modes**: Standard / Compact / Compact + Spectrum, switchable at any time.
- **Hide / Restore Buttons**: long-press any button to enter edit mode, remove it by clicking the red ×; restore anytime from the context menu.
- **Custom Corner Radius / Opacity / Gradient Background** — all up to you.

### 🪟 Other Features
- **System tray** icon, keeps running in the background when minimized.
- **Ambience sound** loops.
- ...

---

## 🖼️ Preview

> The widget floats at the edge of the screen, showing song info, cover, lyrics and spectrum.

### 🎬 Demo Videos
- **Douyin**: [Watch](https://www.douyin.com/user/self?from_tab_name=main&modal_id=7672988738934377763)
- **Bilibili**: [Watch](https://www.bilibili.com/video/BV18muC6aEtc/)

---

## 📥 Download

### GitHub Release (Recommended)
> Always the latest version, published with each update.

📦 **MusicBar Installer**: [Go to the Releases page](https://github.com/lumia-li/Musicbar/releases)

### Download in China
> Lanzou Cloud (蓝奏云) mirror: fast downloads, no login required, ideal for users in mainland China.

📦 **MusicBar Installer**: [Download](https://github.com/lumia-li/Musicbar/blob/main/link.txt)

---

## 🚀 Quick Start
> (ps: optimized for Windows 11, so issues on Windows 10 may not be addressed)

### Requirements
- **Windows 10** or **Windows 11**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Run
```bash
git clone https://github.com/lumia-li/Musicbar.git
cd Musicbar/app
dotnet run
```

### Build Installer
A build script is bundled that publishes and generates an Inno Setup installer in one step:

```bash
build-installer.cmd
```

---

## 🧩 Tech Stack

| Technology / Component | Purpose |
| --- | --- |
| **C# / WPF** | Desktop UI and window interaction |
| **.NET 8** | Target framework |
| **GSMTC** (Windows.Media.Control) | System-level media session control |
| **UI Automation** | Playback "favorite" button auto-detection |
| **NAudio** | Ambience sound engine |
| **Win32 API / P/Invoke** | Topmost, taskbar snap, window interop |

Code is split into feature modules under `app/功能/`:

```
app/功能/default/
├── 播放控制.cs        # Play/pause/skip/playback mode
├── 播放模式.cs
├── 菜单定位.cs
├── 氛围音效播放器.cs   # NAudio ambience sound
├── 歌词.cs            # LRC/KRC parsing & scrolling
├── 进度跳转.cs
├── 进度跳转显示保持.cs
├── 酷狗自动化.cs       # KuGou favorite automation
├── 媒体会话.cs         # GSMTC session management
├── 停靠与拖拽.cs       # Taskbar snap
├── 系统互操作.cs       # Win32 calls
├── 系统托盘.cs
├── 主频谱.cs           # Spectrum visualization
├── 按钮隐藏.cs         # Button-hide edit mode & restore menu
├── 显示模式.cs         # Standard/Compact/Compact+Spectrum layouts
└── 主题与菜单.cs       # Themes & context menu
```

---

## 🗂️ Directory Structure

```
MusicBar/
├── app/                 # Main program source
│   ├── MainWindow.xaml  # Main window UI
│   ├── MainWindow.xaml.cs
│   ├── MusicBar.csproj
│   ├── Assets/          # Icons / fonts
│   └── 功能/            # Feature modules (partial class)
├── installer/           # Inno Setup scripts
├── build-installer.cmd  # One-click installer build
├── cover/               # Cover art assets
└── Sound/               # Ambience sound audio
```

---

## 📄 License

This project is open sourced under the **MIT License**. Feel free to use and modify it.

---

## 💬 Notes

- This project is for **personal learning and legal use only**. Please comply with each music platform's terms of service.
- The "like / favorite" automation relies on each platform's window structure and is currently mostly ineffective; you may hide that button if it misbehaves.
