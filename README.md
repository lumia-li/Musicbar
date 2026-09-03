# 🎵 MusicBar

**简体中文** | [English](README_EN.md)

> 一款可以吸附到 Windows 任务栏上的全局音乐控制悬浮窗，让你在不切走任何窗口的情况下，轻松掌控正在播放的音乐。

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Framework](https://img.shields.io/badge/.NET-8.0-512BD4)
![Language](https://img.shields.io/badge/language-C%23/WPF-178600)
![License](https://img.shields.io/badge/license-MIT-blue)

---

## ✨ 项目简介

**MusicBar** 是一个基于 **C# / WPF（.NET 8）** 开发的 Windows 桌面音乐悬浮控件。它通过系统级 **GSMTC（Global System Media Transport Controls）** 接口与 Windows 自带的多媒体会话机制，无需依赖任何特定播放器的插件或 API，即可统一控制主流的音乐软件。

它常驻在屏幕边缘 / 任务栏旁，像一个「迷你音乐控制条」，无论你在写代码、打游戏还是看视频，都能随时看到当前播放的歌曲，并一键完成**播放 / 暂停、上一首 / 下一首**等操作，真正做到「全局掌控，优雅不打扰」。

---

## 🎯 核心功能

### 🎤 支持的播放器
- **网易云音乐** (NeteaseCloudMusic)
- **酷狗音乐** (KuGouMusic)
- **汽水音乐** (SodaMusic)
- **MoeKoe Music**
- **QQ 音乐**
- **Spotify（未测试）**
- **YouTube Music**

### 🎨 视觉与交互
- **任务栏停靠 / 吸附**：可拖拽到任务栏自动吸附。
- **深浅色主题**：自动跟随系统主题，也可手动切换
- **磨砂玻璃效果**：鼠标悬停时显示亚克力 (Acrylic) 质感
- **频谱可视化**：内置主频谱，播放时随音乐律动
- **三种显示模式**：标准 / 简洁 / 简洁 + 频谱，一键切换
- **按钮隐藏 / 恢复**：长按任意按钮进入编辑模式，点击红色 × 即可隐藏；右键菜单「恢复隐藏按钮」随时找回
- **自定义圆角 / 不透明度 / 渐变背景**，一切由你掌控

### 🪟 其心功能
- **系统托盘**图标，最小化后常驻后台
- **氛围音效**循环播放
- ...

---

## 🖼️ 效果预览

> 播放器悬浮在屏幕边缘，显示歌曲信息、封面、歌词与频谱。

### 🎬 演示视频
- **抖音**：[点击观看](https://www.douyin.com/user/self?from_tab_name=main&modal_id=7672988738934377763)
- **哔哩哔哩**：[点击观看](https://www.bilibili.com/video/BV18muC6aEtc/)

---

## 📥 下载

### GitHub Release（推荐）
> 最新版本，随更新实时发布。

📦 **MusicBar 安装包**：[前往 Releases 页面下载](https://github.com/lumia-li/Musicbar/releases)

### 国内下载
> 蓝奏云网盘，下载速度快，无需登录，适合国内用户。

📦 **MusicBar 安装包**：[点击下载](https://github.com/lumia-li/Musicbar/blob/main/link.txt)

---

## 🚀 快速开始
> (ps:软件针对于 Windows 11 优化,因此在 Windows 10的问题可能处理不到)

### 环境要求
- **Windows 10** 或 **Windows 11**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 运行
```bash
git clone https://github.com/lumia-li/Musicbar.git
cd Musicbar/app
dotnet run
```

### 打包安装
项目内置了构建脚本，可一键发布并生成 Inno Setup 安装包：

```bash
build-installer.cmd
```

---

## 🧩 技术架构

| 技术 / 组件 | 用途 |
| --- | --- |
| **C# / WPF** | 桌面 UI 与窗口交互 |
| **.NET 8** | 目标框架 |
| **GSMTC** (Windows.Media.Control) | 系统级媒体会话控制 |
| **UI Automation** | 播放器收藏按钮自动识别 |
| **NAudio** | 氛围音效播放引擎 |
| **Win32 API / P/Invoke** | 置顶、任务栏吸附、窗口互操作 |

代码按功能模块拆分，位于 `app/功能/` 目录：

```
app/功能/default/
├── 播放控制.cs        # 播放/暂停/切歌/播放模式
├── 播放模式.cs
├── 菜单定位.cs
├── 氛围音效播放器.cs   # NAudio 环境音效
├── 歌词.cs            # LRC/KRC 解析与滚动
├── 进度跳转.cs
├── 进度跳转显示保持.cs
├── 酷狗自动化.cs       # 酷狗收藏自动化
├── 媒体会话.cs         # GSMTC 会话管理
├── 停靠与拖拽.cs       # 任务栏吸附
├── 系统互操作.cs       # Win32 调用
├── 系统托盘.cs
├── 主频谱.cs           # 频谱可视化
├── 按钮隐藏.cs         # 按钮隐藏编辑模式与恢复菜单
├── 显示模式.cs         # 标准 / 简洁 / 简洁+频谱布局切换
└── 主题与菜单.cs       # 深浅色主题/右键菜单
```

---

## 🗂️ 目录结构

```
MusicBar/
├── app/                 # 主程序源码
│   ├── MainWindow.xaml  # 主窗口 UI
│   ├── MainWindow.xaml.cs
│   ├── MusicBar.csproj
│   ├── Assets/          # 图标/字体资源
│   └── 功能/            # 功能模块（partial class）
├── installer/           # Inno Setup 打包脚本
├── build-installer.cmd  # 一键构建安装包
├── cover/               # 封面素材
└── Sound/               # 氛围音效音频
```

---

## 📄 License

本项目采用 **MIT License** 开源，欢迎自由使用与二次开发。

---

## 💬 说明

- 本项目仅用于**个人学习与合法使用**，请遵守各音乐平台的使用条款。
- 播放器「点赞 / 收藏」自动化依赖各平台窗口结构，目前基本无效。如有建议可以隐藏该按钮。
