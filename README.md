# YikeExportTime

Windows 工具，用于导出一刻相册的照片日期清单，并将日期写入已下载的本地照片和视频文件。

## 程序目录

程序、内置 ExifTool 和源码固定存放在 `outputs/YikeExportTime`。运行该目录中的 `一刻日期导出.exe`；后续更新直接覆盖此目录中的对应文件。

## 功能

- 从一刻相册的照片列表请求导出 `photos.json` 和 `photos.csv`
- 保留一刻接口的 `shoot_time`、时区、云端创建和修改日期
- 导出图片和视频，默认移除 `category` 类型限制
- 仅为未检测到媒体拍摄日期的文件写入一刻日期；已有日期的文件跳过
- 图片写入 EXIF/XMP 日期；MP4/MOV 等视频直接修改 QuickTime 时间字段，并同步 Windows 文件日期
- 云端和本地文件名均唯一时直接匹配；仅对重名文件通过 MD5 校验
- 重名、未匹配或仍有歧义的文件不写入

## 使用方式

1. 在一刻相册网页版的开发者工具中，选择 `/youai/file/v1/list` 或 `/youai/file/v2/list` 请求。
2. 将该请求的 URL 和 Cookie 填入导出页，执行新建导出任务。
3. 导出完成后，点击“写入本地日期…”。
4. 选择本次导出的 `photos.json` 和已下载媒体所在目录，点击“开始处理”。

写入功能不生成 `*_original` 备份文件，也不保存预览 CSV。使用前请自行确认本地媒体目录范围。

`v1.2.1` 的 MP4/MOV 写入仅访问容器索引和固定时间字段，不重建完整视频文件，适用于 FNOS SMB 挂载目录。读取已有日期时仅检查与一刻清单匹配的文件，并每 100 个文件显示一次进度。仅缺失拍摄日期的媒体会被写入。

## 构建

源码位于 `src/YikeExporter.cs`。在 Windows 上使用 .NET Framework 编译：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:一刻日期导出.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll src\YikeExporter.cs
```

源码编译后需与发布包中的 `exiftool` 目录一起使用。

## 第三方组件

发布包内含 [ExifTool](https://exiftool.org/)，用于读取和写入照片及视频元数据。其许可文件随发布包中的 `exiftool_files` 目录提供。
