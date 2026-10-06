<p align="center">
  <img src="assets/yike-export-time.png" width="132" alt="YikeExportTime logo">
</p>

<h1 align="center">YikeExportTime</h1>

<p align="center">一刻相册日期导出与本地媒体时间修复工具</p>

<p align="center">
  <a href="#快速开始">快速开始</a> ·
  <a href="#工作方式">工作方式</a> ·
  <a href="#支持范围">支持范围</a> ·
  <a href="docs/INTERFACE.md">界面与截图规范</a>
</p>

> **中文仓库简介（填写到 GitHub About）**：将一刻相册的拍摄日期恢复到本地照片与视频元数据，并同步 Windows 文件时间的 Windows 工具。
>
> **English repository description (for GitHub About)**: A Windows tool that restores Yike Album timestamps to local photo and video metadata and synchronizes Windows file times.

## 它解决什么问题

一刻相册下载后的文件可能缺少拍摄日期，Windows 资源管理器、FNOS 相册或手机图库因而按下载时间排序。YikeExportTime 从你已登录的一刻相册网页请求中导出日期清单，再将对应时间写回已下载的本地媒体。

它只读取你在本机提供的请求 URL、Cookie、`photos.json` 和媒体目录。Cookie 只在运行期间用于请求，不写进导出文件或日志。

## 功能

- 导出一刻相册照片列表为 `photos.json` 与 `photos.csv`
- 保留一刻接口返回的相册日期、时区及云端时间字段
- 根据文件名匹配本地文件；仅对重名文件计算 MD5
- 已有拍摄日期的媒体不改写元数据；缺失日期的媒体才写入
- JPEG、PNG、WebP、MP4、MOV 可快速读取已有日期，避免扫描整个文件内容
- 图片写入 EXIF/XMP 日期；MP4/MOV 只修改 QuickTime 日期字段
- 同步 Windows 创建日期与修改日期，便于资源管理器、FNOS 等按时间排序
- 处理扩展名与真实格式不一致的 PNG/WebP，以及损坏 EXIF 的 JPEG
- 不生成 `*_original` 备份文件，不输出预览 CSV

## 快速开始

### 1. 导出一刻日期清单

1. 登录一刻相册网页版，按 `F12` 打开开发者工具。
2. 在“网络”中选择 `Fetch/XHR`，找到照片列表请求：`/youai/file/v1/list` 或 `/youai/file/v2/list`。
3. 复制该请求的完整 URL，以及请求标头中的 Cookie。
4. 启动 `一刻日期导出.exe`，填写 URL、Cookie 与导出目录，执行导出。

导出完成后会生成 `photos.json`。它是后续写入日期时所需的唯一清单文件。

### 2. 写入本地媒体日期

1. 在导出工具中点击“写入本地日期”。
2. 选择刚导出的 `photos.json`。
3. 选择已下载的照片和视频根目录。
4. 点击“开始处理”。

程序会先匹配文件、识别现有日期，然后写入缺失的媒体日期，并同步 Windows 文件日期。日志会分别显示扫描、日期读取、元数据写入与文件时间同步进度。

## 工作方式

| 阶段 | 行为 |
| --- | --- |
| 日期来源 | 优先使用一刻清单中的相册日期字段 |
| 文件匹配 | 唯一文件名直接匹配；重名时才计算 MD5 |
| 已有日期 | 保留嵌入媒体的日期，并同步到 Windows 文件时间 |
| 缺失日期 | 写入 EXIF/XMP 或 QuickTime 日期，并同步 Windows 文件时间 |
| 无法安全识别 | 跳过且不写入，避免覆盖风险 |

## 支持范围

| 类型 | 读取已有日期 | 写入日期 |
| --- | --- | --- |
| JPEG / JPG | EXIF、XMP | EXIF、XMP |
| PNG / WebP | EXIF、XMP | EXIF、XMP |
| TIFF / 常见 RAW | TIFF/EXIF | 由 ExifTool 处理 |
| MP4 / MOV / M4V / 3GP | QuickTime 容器日期 | QuickTime 容器日期 |
| 其它受支持格式 | 保守跳过或由 ExifTool 兼容处理 | 视文件格式而定 |

## 程序与源码

固定程序目录：

```text
outputs/YikeExportTime/
  一刻日期导出.exe
  exiftool/
  src/YikeExporter.cs
```

Windows 图标源文件为 `assets/yike-export-time.ico`。编译示例：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /win32icon:assets\yike-export-time.ico /out:一刻日期导出.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll src\YikeExporter.cs
```

编译后的程序需与 `exiftool` 目录放在同一目录。

## 界面截图

仓库截图只应使用无 URL、Cookie、本地用户名、目录路径和个人照片的干净界面。截图命名、推荐画面与界面改进建议见 [界面与截图规范](docs/INTERFACE.md)。

## 搜索关键词

建议为 GitHub 仓库添加这些 Topics：

`yike` `yike-album` `baidu-photo` `photo-metadata` `exif` `quicktime` `timestamp` `windows` `fnos` `nas` `photo-backup`

中文关键词：一刻相册、照片日期修复、EXIF 时间、视频拍摄时间、Windows 文件日期、FNOS 相册、NAS 相册整理。

## 第三方组件

发布目录包含 [ExifTool](https://exiftool.org/)，用于兼容读取和写入媒体元数据。其许可文件随 `exiftool_files` 目录提供。
