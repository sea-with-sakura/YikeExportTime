using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace YikeExporter
{
    public class ExportException : Exception
    {
        public ExportException(string message) : base(message) { }
    }

    public static class Json
    {
        public static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 80 };
        }
        public static Dictionary<string, object> Object(object value)
        {
            return value as Dictionary<string, object>;
        }
        public static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }
        public static string Text(object value)
        {
            return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        public static long? Number(object value)
        {
            long number;
            return Int64.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? (long?)number : null;
        }
        public static bool Flag(object value)
        {
            return value is bool ? (bool)value : Text(value) == "1" || Text(value).Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class Photo
    {
        public string filename { get; set; }
        public string cloud_path { get; set; }
        public string file_id { get; set; }
        public long? size_bytes { get; set; }
        public string md5_from_response { get; set; }
        public long? album_time_unix_seconds { get; set; }
        public string album_time_china { get; set; }
        public string source_shoot_tz { get; set; }
        public string source_extra_date_time { get; set; }
        public long? cloud_ctime_unix_seconds { get; set; }
        public string cloud_ctime_china { get; set; }
        public long? cloud_mtime_unix_seconds { get; set; }
        public string cloud_mtime_china { get; set; }
        public string width { get; set; }
        public string height { get; set; }
        public string Key()
        {
            return String.IsNullOrEmpty(file_id) ? "path:" + cloud_path + "|" + md5_from_response + "|" + size_bytes : "id:" + file_id;
        }
        public static string Date(long? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0) return "";
            try
            {
                DateTimeOffset value = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds.Value).ToOffset(TimeSpan.FromHours(8));
                return value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException) { return ""; }
        }
        public static Photo Parse(Dictionary<string, object> item)
        {
            string path = Json.Text(Json.Get(item, "path"));
            if (String.IsNullOrEmpty(path)) throw new ExportException("返回记录缺少 path，不能可靠匹配文件；已停止。");
            Dictionary<string, object> extra = Json.Object(Json.Get(item, "extra_info"));
            long? shoot = Json.Number(Json.Get(item, "shoot_time"));
            long? created = Json.Number(Json.Get(item, "ctime"));
            long? modified = Json.Number(Json.Get(item, "mtime"));
            int lastSlash = path.LastIndexOf('/');
            return new Photo {
                filename = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path,
                cloud_path = path,
                file_id = Json.Text(Json.Get(item, "fsid")),
                size_bytes = Json.Number(Json.Get(item, "size")),
                md5_from_response = Json.Text(Json.Get(item, "md5")),
                album_time_unix_seconds = shoot,
                album_time_china = Date(shoot),
                source_shoot_tz = Json.Text(Json.Get(item, "shoot_tz")),
                source_extra_date_time = Json.Text(Json.Get(extra, "date_time")),
                cloud_ctime_unix_seconds = created,
                cloud_ctime_china = Date(created),
                cloud_mtime_unix_seconds = modified,
                cloud_mtime_china = Date(modified),
                width = Json.Text(Json.Get(extra, "width")),
                height = Json.Text(Json.Get(extra, "height"))
            };
        }
    }

    public sealed class Page
    {
        public List<Photo> Photos;
        public bool HasMore;
        public string Cursor;
        public static Page Parse(string text)
        {
            Dictionary<string, object> root;
            try { root = Json.Serializer().Deserialize<Dictionary<string, object>>(text.TrimStart('\uFEFF')); }
            catch { throw new ExportException("返回内容不是有效的 JSON。请确认请求地址是照片列表，且登录仍有效。"); }
            long? errno = Json.Number(Json.Get(root, "errno"));
            if (!errno.HasValue) throw new ExportException("响应没有 errno，可能是登录页或选错了请求。");
            if (errno.Value != 0) throw new ExportException("一刻接口返回错误码 " + errno.Value + "。请重新登录后复制新的请求地址和 Cookie。");
            IEnumerable rawList = Json.Get(root, "list") as IEnumerable;
            if (rawList == null || rawList is string) throw new ExportException("响应缺少照片 list；请选取与已验证响应相同的照片列表请求。");
            if (Json.Get(root, "has_more") == null) throw new ExportException("响应没有 has_more，无法判断是否完整；已停止。");
            List<Photo> photos = new List<Photo>();
            foreach (object value in rawList)
            {
                Dictionary<string, object> item = Json.Object(value);
                if (item == null) throw new ExportException("照片列表结构异常；已停止。");
                photos.Add(Photo.Parse(item));
            }
            Page page = new Page { Photos = photos, HasMore = Json.Flag(Json.Get(root, "has_more")), Cursor = Json.Text(Json.Get(root, "cursor")) };
            if (page.HasMore && (photos.Count == 0 || String.IsNullOrEmpty(page.Cursor)))
                throw new ExportException("接口表示还有下一页，但没有返回照片或分页游标；已停止，避免死循环。");
            return page;
        }
    }

    public sealed class Options
    {
        public string RequestUrl;
        public string Cookie;
        public string Directory;
        public double DelaySeconds;
        public bool Resume;
        public static string ValidateUrl(string text)
        {
            Uri uri;
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                !uri.Host.Equals("photo.baidu.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment))
                throw new ExportException("请求地址必须是 https://photo.baidu.com 的地址。");
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 4 || parts[0] != "youai" || parts[1] != "file" ||
                (parts[2] != "v1" && parts[2] != "v2") || parts[3] != "list")
                throw new ExportException("请选择 /youai/file/v1/list 或 /youai/file/v2/list 照片列表请求。其他接口不执行。");
            return uri.AbsoluteUri;
        }
        public static string NormalizeCookie(string text)
        {
            string cookie = text.Trim();
            if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) cookie = cookie.Substring(7).Trim();
            if (String.IsNullOrEmpty(cookie) || cookie.IndexOf('\n') >= 0 || cookie.IndexOf('\r') >= 0)
                throw new ExportException("请粘贴请求标头中完整的 Cookie 值，不能粘贴整个请求或多行文本。");
            return cookie;
        }
        public static string UrlForCursor(string original, string cursor)
        {
            UriBuilder builder = new UriBuilder(ValidateUrl(original));
            // Keep the original escaping and duplicate parameters; only replace cursor.
            List<string> parameters = new List<string>();
            foreach (string parameter in builder.Query.TrimStart('?').Split('&'))
            {
                if (parameter.Length == 0) continue;
                int equals = parameter.IndexOf('=');
                string name = equals >= 0 ? parameter.Substring(0, equals) : parameter;
                if (!HttpUtility.UrlDecode(name).Equals("cursor", StringComparison.OrdinalIgnoreCase)) parameters.Add(parameter);
            }
            parameters.Add("cursor=" + Uri.EscapeDataString(cursor ?? ""));
            builder.Query = String.Join("&", parameters);
            return builder.Uri.AbsoluteUri;
        }
        public static string SourcePath(string url) { return new Uri(ValidateUrl(url)).AbsolutePath; }
        public static bool HasCategory(string url)
        {
            return HttpUtility.ParseQueryString(new Uri(ValidateUrl(url)).Query).AllKeys
                .Any(k => String.Equals(k, "category", StringComparison.OrdinalIgnoreCase));
        }
        public static string WithoutCategory(string url)
        {
            UriBuilder builder = new UriBuilder(ValidateUrl(url));
            builder.Query = String.Join("&", builder.Query.TrimStart('?').Split('&').Where(parameter => {
                int equals = parameter.IndexOf('=');
                string name = equals >= 0 ? parameter.Substring(0, equals) : parameter;
                return parameter.Length > 0 && !HttpUtility.UrlDecode(name).Equals("category", StringComparison.OrdinalIgnoreCase);
            }));
            return builder.Uri.AbsoluteUri;
        }
        public static string RequestFingerprint(string url)
        {
            // Store a hash, not the address. Cursor changes do not change the request scope.
            return Engine.Fingerprint(UrlForCursor(url, ""));
        }
    }

    public sealed class MediaCounts
    {
        public int Images;
        public int Videos;
        public int Others;
        public MediaCounts(IEnumerable<Photo> photos)
        {
            string[] images = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".avif", ".dng", ".raw", ".cr2", ".cr3", ".nef", ".arw" };
            string[] videos = { ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v", ".3gp", ".flv", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg", ".vob" };
            foreach (Photo photo in photos)
            {
                string extension = Path.GetExtension(photo.filename ?? "").ToLowerInvariant();
                if (images.Contains(extension)) Images++;
                else if (videos.Contains(extension)) Videos++;
                else Others++;
            }
        }
        public override string ToString() { return "图片 " + Images + "，视频 " + Videos + "，其他 " + Others + "（按扩展名统计）"; }
    }

    public interface ITransport
    {
        string Fetch(string url, string cookie, CancellationToken cancellation);
    }

    public sealed class HttpTransport : ITransport
    {
        public string Fetch(string url, string cookie, CancellationToken cancellation)
        {
            Options.ValidateUrl(url);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.AllowAutoRedirect = false;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.Timeout = 45000;
                request.ReadWriteTimeout = 45000;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
                request.Accept = "application/json, text/plain, */*";
                request.Referer = "https://photo.baidu.com/photo/web/home";
                request.Headers[HttpRequestHeader.Cookie] = cookie;
                int retrySeconds = 2 << attempt;
                using (cancellation.Register(request.Abort))
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status >= 300 && status < 400)
                                throw new ExportException("接口重定向，可能登录已过期。请重新获取请求地址和 Cookie。");
                            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                            {
                                StringBuilder result = new StringBuilder();
                                char[] buffer = new char[8192];
                                int read;
                                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    result.Append(buffer, 0, read);
                                    if (result.Length > 32 * 1024 * 1024)
                                        throw new ExportException("单页响应超过 32 MB，请减小原请求的 limit 后重试。");
                                }
                                return result.ToString();
                            }
                        }
                    }
                    catch (WebException ex)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        HttpWebResponse errorResponse = ex.Response as HttpWebResponse;
                        int status = errorResponse == null ? 0 : (int)errorResponse.StatusCode;
                        if (errorResponse != null)
                        {
                            int retryAfter;
                            if (Int32.TryParse(errorResponse.Headers["Retry-After"], out retryAfter))
                                retrySeconds = Math.Max(retrySeconds, Math.Min(60, Math.Max(1, retryAfter)));
                            errorResponse.Dispose();
                        }
                        if (status == 401 || status == 403)
                            throw new ExportException("接口拒绝访问（HTTP " + status + "）。请重新登录并复制当前请求地址与 Cookie。");
                        bool transient = status == 0 || status == 429 || status >= 500;
                        if (!transient || attempt == 3)
                            throw new ExportException(status == 0 ? "网络请求失败；已保存进度，可稍后继续。" : "接口返回 HTTP " + status + "；已保存进度。");
                    }
                }
                if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(retrySeconds))) cancellation.ThrowIfCancellationRequested();
            }
            throw new ExportException("网络请求失败。");
        }
    }

    public sealed class Checkpoint
    {
        public int schema_version { get; set; }
        public string source_path { get; set; }
        public string request_fingerprint { get; set; }
        public string credential_fingerprint { get; set; }
        public string next_cursor { get; set; }
        public bool complete { get; set; }
        public int pages_fetched { get; set; }
        public long committed_bytes { get; set; }
    }

    public sealed class Result
    {
        public bool Complete;
        public bool Cancelled;
        public string Error;
        public int Count;
        public int Pages;
        public string Directory;
        public string MediaSummary;
    }

    public sealed class Engine
    {
        private readonly ITransport transport;
        public Engine(ITransport transport) { this.transport = transport; }
        public static string Fingerprint(string cookie)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(cookie))).Replace("-", "");
        }
        public static void AtomicText(string path, string content, bool bom)
        {
            string temp = path + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(bom)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        public static string CsvCell(string value)
        {
            value = value ?? "";
            string probe = value.TrimStart(' ', '\t', '\r', '\n');
            if (probe.Length > 0 && "=+-@".IndexOf(probe[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        public static void Export(string directory, List<Photo> photos, Checkpoint state, string error, bool cancelled)
        {
            MediaCounts media = new MediaCounts(photos);
            object document = new {
                source = "一刻相册照片列表接口",
                exported_at = DateTimeOffset.Now.ToString("o"),
                complete = state.complete,
                cancelled = cancelled,
                error = error,
                record_count = photos.Count,
                completion_note = "complete 仅表示本次请求范围分页结束，不保证覆盖整个账号。",
                image_count_by_extension = media.Images,
                video_count_by_extension = media.Videos,
                other_count_by_extension = media.Others,
                pages_fetched = state.pages_fetched,
                display_timezone = "Asia/Shanghai (+08:00)",
                date_note = "album_time 来自一刻 shoot_time，不保证是真实拍摄时间。缺失值保留为空，不使用 ctime 或 mtime 替代。",
                matching_note = "MD5 来自接口；修改照片之前，先验证它与下载原文件的 MD5 是否一致。",
                photos = photos
            };
            AtomicText(Path.Combine(directory, "photos.json"), Json.Serializer().Serialize(document), false);
            StringBuilder csv = new StringBuilder();
            csv.AppendLine("filename,cloud_path,file_id,size_bytes,md5_from_response,album_time_unix_seconds,album_time_china,source_shoot_tz,source_extra_date_time,cloud_ctime_unix_seconds,cloud_ctime_china,cloud_mtime_unix_seconds,cloud_mtime_china,width,height");
            foreach (Photo p in photos)
            {
                string[] values = { p.filename, p.cloud_path, "'" + p.file_id, Json.Text(p.size_bytes), p.md5_from_response,
                    Json.Text(p.album_time_unix_seconds), p.album_time_china, p.source_shoot_tz, p.source_extra_date_time,
                    Json.Text(p.cloud_ctime_unix_seconds), p.cloud_ctime_china, Json.Text(p.cloud_mtime_unix_seconds), p.cloud_mtime_china, p.width, p.height };
                csv.AppendLine(String.Join(",", values.Select(CsvCell)));
            }
            AtomicText(Path.Combine(directory, "photos.csv"), csv.ToString(), true);
            int duplicateNames = photos.GroupBy(p => p.filename, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
            int missingDates = photos.Count(p => String.IsNullOrEmpty(p.album_time_china));
            StringBuilder summary = new StringBuilder();
            summary.AppendLine("一刻相册日期清单");
            summary.AppendLine("状态：" + (state.complete ? "本次请求范围分页已结束；请对照相册总数核验" : "尚未完成，当前是部分清单"));
            summary.AppendLine("记录数：" + photos.Count + "；已保存页数：" + state.pages_fetched);
            summary.AppendLine(media.ToString());
            summary.AppendLine("缺少可用日期：" + missingDates + "；重名组数：" + duplicateNames);
            if (!String.IsNullOrEmpty(error)) summary.AppendLine("停止原因：" + error);
            if (cancelled) summary.AppendLine("已按你的操作停止，可使用同一账号继续任务。");
            summary.AppendLine();
            summary.AppendLine("photos.csv 可用 Excel 查看；file_id 带单引号，防止 Excel 改写长数字。程序匹配请使用 photos.json。");
            summary.AppendLine("album_time_china 是 shoot_time 按北京时间转换；原始 shoot_time、shoot_tz 和 extra_info.date_time 另外保留。");
            summary.AppendLine("该日期是相册记录，不保证每张照片的真实拍摄时间。没有日期时不会拿云端创建时间替代。");
            summary.AppendLine("本程序只读取照片列表，不下载图片，不修改照片，也不写入 NAS。");
            summary.AppendLine("checkpoint.json 与 records.ndjson 用于继续任务，请保留在同一目录。Cookie 和完整请求地址不会保存。");
            summary.AppendLine("接口分页不是固定快照；导出期间尽量不要增删照片。完成仅表示接口返回 has_more=0，建议对照相册数量检查。");
            AtomicText(Path.Combine(directory, "导出说明.txt"), summary.ToString(), true);
        }
        public Result Run(Options options, Action<string> log, CancellationToken cancellation)
        {
            options.RequestUrl = Options.ValidateUrl(options.RequestUrl);
            options.Cookie = Options.NormalizeCookie(options.Cookie);
            if (options.DelaySeconds < 1 || options.DelaySeconds > 30) throw new ExportException("翻页间隔应为 1–30 秒。");
            string directory = Path.GetFullPath(options.Directory);
            Directory.CreateDirectory(directory);
            string checkpointPath = Path.Combine(directory, "checkpoint.json");
            string journalPath = Path.Combine(directory, "records.ndjson");
            string sourcePath = Options.SourcePath(options.RequestUrl);
            string fingerprint = Fingerprint(options.Cookie);
            using (FileStream taskLock = new FileStream(Path.Combine(directory, ".task.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Checkpoint state;
                List<Photo> photos = new List<Photo>();
                HashSet<string> knownPhotos = new HashSet<string>(StringComparer.Ordinal);
                if (options.Resume)
                {
                    if (!File.Exists(checkpointPath) || !File.Exists(journalPath))
                        throw new ExportException("所选目录缺少 checkpoint.json 或 records.ndjson，请选择工具生成的任务目录。");
                    try { state = Json.Serializer().Deserialize<Checkpoint>(File.ReadAllText(checkpointPath, Encoding.UTF8)); }
                    catch { throw new ExportException("进度文件无法读取；请保留原文件并新建任务。"); }
                    if (state == null || state.schema_version != 1 || state.source_path != sourcePath || state.committed_bytes < 0)
                        throw new ExportException("进度文件版本或照片接口不匹配；请新建任务。");
                    if (state.credential_fingerprint != fingerprint)
                        throw new ExportException("本次 Cookie 与任务开始时不同。为避免混合账号，请用原 Cookie 继续，或用新的 Cookie 新建任务。");
                    if (String.IsNullOrEmpty(state.request_fingerprint))
                        throw new ExportException("旧版任务未记录请求范围。请用旧版继续原任务，或在新版开始新任务；原清单会保留。");
                    if (state.request_fingerprint != Options.RequestFingerprint(options.RequestUrl))
                        throw new ExportException("请求地址或类型筛选与原任务不同。请保持原地址和原选项继续，或开始新任务导出新的范围。");
                    using (FileStream journal = new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        if (journal.Length < state.committed_bytes) throw new ExportException("记录文件不完整，不能继续任务。");
                        // Discard an uncommitted page after an interrupted checkpoint write.
                        if (journal.Length != state.committed_bytes) journal.SetLength(state.committed_bytes);
                    }
                    try
                    {
                        foreach (string line in File.ReadLines(journalPath, Encoding.UTF8))
                        {
                            if (String.IsNullOrWhiteSpace(line)) continue;
                            Photo photo = Json.Serializer().Deserialize<Photo>(line);
                            if (photo == null || String.IsNullOrEmpty(photo.cloud_path)) throw new Exception();
                            if (knownPhotos.Add(photo.Key())) photos.Add(photo);
                        }
                    }
                    catch { throw new ExportException("已保存记录无法读取；请保留文件并新建任务。"); }
                    log("已恢复 " + photos.Count + " 条记录，已保存 " + state.pages_fetched + " 页。");
                }
                else
                {
                    if (File.Exists(checkpointPath) || File.Exists(journalPath)) throw new ExportException("任务目录已有记录，请选择继续任务，或新建一个目录。");
                    state = new Checkpoint { schema_version = 1, source_path = sourcePath, credential_fingerprint = fingerprint, request_fingerprint = Options.RequestFingerprint(options.RequestUrl),
                        next_cursor = "", complete = false, pages_fetched = 0, committed_bytes = 0 };
                    File.WriteAllText(journalPath, "", new UTF8Encoding(false));
                    AtomicText(checkpointPath, Json.Serializer().Serialize(state), false);
                }
                HashSet<string> seenCursors = new HashSet<string>(StringComparer.Ordinal);
                Result result = new Result { Directory = directory };
                try
                {
                    while (!state.complete)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!seenCursors.Add(state.next_cursor)) throw new ExportException("接口重复返回分页游标；已停止，避免循环。");
                        log("正在读取第 " + (state.pages_fetched + 1) + " 页……");
                        string requestUrl = Options.UrlForCursor(options.RequestUrl, state.next_cursor);
                        Page page = Page.Parse(transport.Fetch(requestUrl, options.Cookie, cancellation));
                        if (page.HasMore && (page.Cursor == state.next_cursor || seenCursors.Contains(page.Cursor)))
                            throw new ExportException("接口分页没有推进；已停止，避免重复读取。");
                        List<Photo> additions = new List<Photo>();
                        HashSet<string> pageKeys = new HashSet<string>(StringComparer.Ordinal);
                        foreach (Photo photo in page.Photos)
                            if (!knownPhotos.Contains(photo.Key()) && pageKeys.Add(photo.Key())) additions.Add(photo);
                        if (page.HasMore && additions.Count == 0)
                            throw new ExportException("这一页全部是已读取记录，且仍声称有下一页；已停止，请检查接口或重新导出。");
                        StringBuilder lines = new StringBuilder();
                        foreach (Photo photo in additions) lines.Append(Json.Serializer().Serialize(photo)).Append('\n');
                        byte[] bytes = Encoding.UTF8.GetBytes(lines.ToString());
                        long committedBytes;
                        using (FileStream journal = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                        {
                            journal.Write(bytes, 0, bytes.Length);
                            journal.Flush(true);
                            committedBytes = journal.Length;
                        }
                        Checkpoint next = new Checkpoint { schema_version = 1, source_path = sourcePath, credential_fingerprint = fingerprint, request_fingerprint = state.request_fingerprint,
                            next_cursor = page.HasMore ? page.Cursor : "", complete = !page.HasMore,
                            pages_fetched = state.pages_fetched + 1, committed_bytes = committedBytes };
                        AtomicText(checkpointPath, Json.Serializer().Serialize(next), false);
                        state = next;
                        foreach (Photo photo in additions) { knownPhotos.Add(photo.Key()); photos.Add(photo); }
                        log("第 " + state.pages_fetched + " 页已保存：新增 " + additions.Count + " 条，累计 " + photos.Count + " 条。");
                        if (!state.complete && cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(options.DelaySeconds)))
                            cancellation.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException) { result.Cancelled = true; }
                catch (ExportException ex) { result.Error = ex.Message; }
                catch (IOException) { result.Error = "文件保存失败，请检查磁盘空间和目录权限。保留任务目录以便恢复。"; }
                catch { result.Error = "发生未预期错误；请保留任务目录。登录凭据未写入日志。"; }
                Export(directory, photos, state, result.Error, result.Cancelled);
                result.Complete = state.complete;
                result.Count = photos.Count;
                result.Pages = state.pages_fetched;
                result.MediaSummary = new MediaCounts(photos).ToString();
                log(result.MediaSummary);
                log(state.complete ? "本次请求范围已读取完毕，共 " + photos.Count + " 条；请对照相册总数核验。" : "已保存部分清单，共 " + photos.Count + " 条；任务尚未完成。");
                return result;
            }
        }
    }

    public sealed class MainForm : Form
    {
        private TextBox urlBox, cookieBox, folderBox, logBox;
        private NumericUpDown delayBox;
        private CheckBox allMediaBox;
        private Button startButton, resumeButton, stopButton, openButton, dateWriteButton;
        private ProgressBar progress;
        private CancellationTokenSource cancel;
        private bool running;
        private string lastDirectory;
        public MainForm()
        {
            Text = "一刻相册 · 照片日期清单导出与写入 v1.2.6";
            Size = new Size(900, 810);
            MinimumSize = new Size(900, 760);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = Color.FromArgb(248, 250, 253);
            AutoScaleMode = AutoScaleMode.Dpi;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 12 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            float[] heights = { 44, 65, 28, 38, 28, 42, 58, 80, 38, 16, 28 };
            foreach (float height in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "一刻相册日期清单导出", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(new Label { Text = "在一刻相册网页版按 F12 → 网络 → Fetch/XHR，选择照片列表 list 请求。\n从请求标头复制请求 URL 和 Cookie，并分别填写。", Dock = DockStyle.Fill }, 0, 1);
            layout.Controls.Add(new Label { Text = "① 照片列表请求 URL", Dock = DockStyle.Fill }, 0, 2);
            urlBox = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(urlBox, 0, 3);
            layout.Controls.Add(new Label { Text = "② Cookie（仅在运行时使用，不写入导出文件）", Dock = DockStyle.Fill }, 0, 4);
            cookieBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            layout.Controls.Add(cookieBox, 0, 5);
            TableLayoutPanel folderPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            folderPanel.Controls.Add(new Label { Text = "③ 导出目录（每次任务创建独立目录）", Dock = DockStyle.Fill }, 0, 0);
            folderPanel.SetColumnSpan(folderPanel.GetControlFromPosition(0, 0), 2);
            folderBox = new TextBox { Dock = DockStyle.Fill, Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exports") };
            Button browse = new Button { Text = "选择…", Dock = DockStyle.Fill };
            browse.Click += delegate { using (FolderBrowserDialog dialog = new FolderBrowserDialog()) { if (dialog.ShowDialog(this) == DialogResult.OK) folderBox.Text = dialog.SelectedPath; } };
            folderPanel.Controls.Add(folderBox, 0, 1);
            folderPanel.Controls.Add(browse, 1, 1);
            layout.Controls.Add(folderPanel, 0, 6);
            FlowLayoutPanel settings = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            settings.Controls.Add(new Label { Text = "翻页间隔（秒）", AutoSize = true, Margin = new Padding(0, 5, 8, 0) });
            delayBox = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 1, Width = 65 };
            settings.Controls.Add(delayBox);
            settings.Controls.Add(new Label { Text = "仅导出清单，不下载或修改云端文件。", AutoSize = true, Margin = new Padding(18, 5, 0, 0), ForeColor = Color.FromArgb(70, 80, 95) });
            settings.SetFlowBreak(settings.Controls[settings.Controls.Count - 1], true);
            allMediaBox = new CheckBox { Text = "导出图片和视频（移除 category 类型限制，保留其他请求参数）", Checked = true, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            settings.Controls.Add(allMediaBox);
            layout.Controls.Add(settings, 0, 7);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
            startButton = Button("新建导出任务", 130);
            resumeButton = Button("继续导出任务…", 150);
            stopButton = Button("停止并保存", 130); stopButton.Enabled = false;
            openButton = Button("打开任务目录", 140); openButton.Enabled = false;
            dateWriteButton = Button("写入本地日期…", 145);
            buttons.Controls.AddRange(new Control[] { startButton, resumeButton, stopButton, openButton, dateWriteButton });
            startButton.Click += async delegate { await Start(false); };
            resumeButton.Click += async delegate { await Start(true); };
            stopButton.Click += delegate { if (cancel != null) { cancel.Cancel(); stopButton.Enabled = false; AddLog("正在停止并保存当前进度……"); } };
            openButton.Click += delegate { if (lastDirectory != null && Directory.Exists(lastDirectory)) System.Diagnostics.Process.Start("explorer.exe", "\"" + lastDirectory + "\""); };
            dateWriteButton.Click += delegate { using (DateWriteForm form = new DateWriteForm()) form.ShowDialog(this); };
            layout.Controls.Add(buttons, 0, 8);
            progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Blocks };
            layout.Controls.Add(progress, 0, 9);
            layout.Controls.Add(new Label { Text = "处理状态", Dock = DockStyle.Fill }, 0, 10);
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9F) };
            layout.Controls.Add(logBox, 0, 11);
            AddLog("就绪。任务将生成 photos.csv、photos.json 和恢复所需的进度文件。 ");
            FormClosing += delegate(object sender, FormClosingEventArgs args) {
                if (running) { args.Cancel = true; if (cancel != null) cancel.Cancel(); AddLog("正在保存进度。任务停止后可关闭窗口。 "); }
            };
        }
        private Button Button(string text, int width)
        {
            return new Button { Text = text, Width = width, Height = 32, Margin = new Padding(0, 0, 10, 0) };
        }
        private void AddLog(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), text); return; }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        }
        private async Task Start(bool resume)
        {
            if (running) return;
            Options options;
            try
            {
                string requestUrl = Options.ValidateUrl(urlBox.Text);
                if (allMediaBox.Checked) requestUrl = Options.WithoutCategory(requestUrl);
                string cookie = Options.NormalizeCookie(cookieBox.Text);
                string folder;
                if (resume)
                {
                    using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择已有任务目录（包含 checkpoint.json 和 records.ndjson）；继续时请使用原 Cookie。" })
                    { if (dialog.ShowDialog(this) != DialogResult.OK) return; folder = dialog.SelectedPath; }
                }
                else
                {
                    if (String.IsNullOrWhiteSpace(folderBox.Text)) throw new ExportException("请选择导出位置。");
                    folder = Path.Combine(Path.GetFullPath(folderBox.Text.Trim()), "YikeExport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                }
                options = new Options { RequestUrl = requestUrl, Cookie = cookie, Directory = folder, DelaySeconds = (double)delayBox.Value, Resume = resume };
            }
            catch (Exception ex) { MessageBox.Show(this, ex is ExportException ? ex.Message : "输入无效，请检查导出路径。", "检查输入", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            running = true;
            startButton.Enabled = resumeButton.Enabled = urlBox.Enabled = cookieBox.Enabled = delayBox.Enabled = allMediaBox.Enabled = false;
            stopButton.Enabled = true;
            progress.Style = ProgressBarStyle.Marquee;
            cancel = new CancellationTokenSource();
            AddLog(resume ? "正在继续导出任务。" : "正在读取照片列表。 ");
            AddLog(allMediaBox.Checked ? "媒体范围：图片和视频。" : "媒体范围：使用原请求类型限制。 ");
            try
            {
                Result result = await Task.Run(() => new Engine(new HttpTransport()).Run(options, AddLog, cancel.Token));
                lastDirectory = result.Directory;
                openButton.Enabled = true;
                AddLog("清单已写入任务目录。 ");
                if (!String.IsNullOrEmpty(result.Error)) AddLog(result.Error);
                if (result.Complete) MessageBox.Show(this, "本次请求范围已读取完毕，共 " + result.Count + " 条。\n" + result.MediaSummary + "\n请对照相册总数核验；数量偏少时，在网页版选择全部并重新复制 list 请求，开始新任务。", "本次请求读取完毕", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (ExportException ex) { AddLog(ex.Message); }
            catch (IOException) { AddLog("无法访问任务目录；它可能正被另一个导出窗口使用，或目录不可写。"); }
            catch { AddLog("运行失败，请检查目录权限。凭据和完整地址不会输出到日志。"); }
            finally
            {
                running = false;
                startButton.Enabled = resumeButton.Enabled = urlBox.Enabled = cookieBox.Enabled = delayBox.Enabled = allMediaBox.Enabled = true;
                stopButton.Enabled = false;
                progress.Style = ProgressBarStyle.Blocks;
                cancel.Dispose(); cancel = null;
                // Avoid retaining credentials in a completed/failed task closure.
                options.Cookie = null; options.RequestUrl = null;
            }
        }
    }

    public sealed class LocalFile
    {
        public string Path;
        public string Name;
        public string Md5;
    }

    public sealed class DatePlanItem
    {
        public LocalFile Local;
        public Photo Cloud;
        public string MatchMethod;
        public DateTimeOffset AlbumDate;
        public DateTimeOffset? ExistingDate;
        public DateTimeOffset TargetDate;
        public bool IsVideo;
        public bool DateWritten;
    }

    public sealed class DatePlan
    {
        public List<DatePlanItem> Items = new List<DatePlanItem>();
        public List<DatePlanItem> FileTimeItems = new List<DatePlanItem>();
        public int FilesScanned;
        public int Md5FilesChecked;
        public int ExistingDateSkipped;
        public int Md5Matches;
        public int NameMatches;
        public int Unmatched;
        public int Ambiguous;
        public int Unsupported;
    }

    public static class QuickTimeDatePatcher
    {
        private static readonly HashSet<string> Containers = new HashSet<string>(new[] { "moov", "trak", "mdia" }, StringComparer.Ordinal);
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static uint ReadUInt32(byte[] value, int offset)
        {
            return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) | ((uint)value[offset + 2] << 8) | value[offset + 3];
        }
        private static ulong ReadUInt64(byte[] value, int offset)
        {
            ulong result = 0;
            for (int i = 0; i < 8; i++) result = (result << 8) | value[offset + i];
            return result;
        }
        private static void WriteUInt32(FileStream file, long offset, uint value)
        {
            file.Position = offset;
            file.WriteByte((byte)(value >> 24)); file.WriteByte((byte)(value >> 16)); file.WriteByte((byte)(value >> 8)); file.WriteByte((byte)value);
        }
        private static void WriteUInt64(FileStream file, long offset, ulong value)
        {
            byte[] bytes = new byte[8];
            for (int i = 7; i >= 0; i--) { bytes[i] = (byte)value; value >>= 8; }
            file.Position = offset; file.Write(bytes, 0, bytes.Length);
        }
        private static bool ReadExactly(FileStream file, byte[] buffer)
        {
            int offset = 0, count;
            while (offset < buffer.Length && (count = file.Read(buffer, offset, buffer.Length - offset)) > 0) offset += count;
            return offset == buffer.Length;
        }
        private static int PatchDateAtom(FileStream file, long atomOffset, long atomSize, int headerSize, ulong seconds)
        {
            long dataOffset = atomOffset + headerSize;
            if (atomSize < headerSize + 12) return 0;
            file.Position = dataOffset;
            int version = file.ReadByte();
            if (version == 0)
            {
                if (seconds > UInt32.MaxValue || atomSize < headerSize + 12) return 0;
                WriteUInt32(file, dataOffset + 4, (uint)seconds);
                WriteUInt32(file, dataOffset + 8, (uint)seconds);
                return 1;
            }
            if (version == 1)
            {
                if (atomSize < headerSize + 20) return 0;
                WriteUInt64(file, dataOffset + 4, seconds);
                WriteUInt64(file, dataOffset + 12, seconds);
                return 1;
            }
            return 0;
        }
        private static int Scan(FileStream file, long start, long end, ulong seconds)
        {
            int changed = 0;
            long position = start;
            byte[] header = new byte[16];
            byte[] basic = new byte[8];
            while (position + 8 <= end)
            {
                file.Position = position;
                if (!ReadExactly(file, basic)) break;
                ulong size = ReadUInt32(basic, 0);
                string type = Encoding.ASCII.GetString(basic, 4, 4);
                int headerSize = 8;
                if (size == 1)
                {
                    file.Position = position;
                    if (!ReadExactly(file, header)) break;
                    size = ReadUInt64(header, 8); headerSize = 16;
                }
                else if (size == 0) size = (ulong)(end - position);
                if (size < (ulong)headerSize || size > (ulong)(end - position)) break;
                long atomSize = (long)size;
                if (type == "mvhd" || type == "tkhd" || type == "mdhd") changed += PatchDateAtom(file, position, atomSize, headerSize, seconds);
                else if (Containers.Contains(type)) changed += Scan(file, position + headerSize, position + atomSize, seconds);
                position += atomSize;
            }
            return changed;
        }
        private static DateTimeOffset? ReadDateAtom(FileStream file, long atomOffset, long atomSize, int headerSize)
        {
            long dataOffset = atomOffset + headerSize;
            if (atomSize < headerSize + 12) return null;
            file.Position = dataOffset;
            int version = file.ReadByte();
            if (version == 0)
            {
                byte[] value = new byte[4];
                file.Position = dataOffset + 4;
                if (!ReadExactly(file, value)) return null;
                uint seconds = ReadUInt32(value, 0);
                return seconds == 0 ? (DateTimeOffset?)null : Epoch.AddSeconds(seconds);
            }
            if (version == 1)
            {
                byte[] value = new byte[8];
                file.Position = dataOffset + 4;
                if (!ReadExactly(file, value)) return null;
                ulong seconds = ReadUInt64(value, 0);
                return seconds == 0 || seconds > Int64.MaxValue ? (DateTimeOffset?)null : Epoch.AddSeconds((long)seconds);
            }
            return null;
        }
        private static DateTimeOffset? ScanForDate(FileStream file, long start, long end)
        {
            DateTimeOffset? earliest = null;
            long position = start;
            byte[] header = new byte[16];
            byte[] basic = new byte[8];
            while (position + 8 <= end)
            {
                file.Position = position;
                if (!ReadExactly(file, basic)) break;
                ulong size = ReadUInt32(basic, 0);
                string type = Encoding.ASCII.GetString(basic, 4, 4);
                int headerSize = 8;
                if (size == 1)
                {
                    file.Position = position;
                    if (!ReadExactly(file, header)) break;
                    size = ReadUInt64(header, 8); headerSize = 16;
                }
                else if (size == 0) size = (ulong)(end - position);
                if (size < (ulong)headerSize || size > (ulong)(end - position)) break;
                long atomSize = (long)size;
                DateTimeOffset? date = null;
                if (type == "mvhd" || type == "tkhd" || type == "mdhd") date = ReadDateAtom(file, position, atomSize, headerSize);
                else if (Containers.Contains(type)) date = ScanForDate(file, position + headerSize, position + atomSize);
                if (date.HasValue && (!earliest.HasValue || date.Value < earliest.Value)) earliest = date;
                position += atomSize;
            }
            return earliest;
        }
        public static bool TryReadCreatedDate(string path, out DateTimeOffset? date)
        {
            date = null;
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] header = new byte[12];
                    if (!ReadExactly(file, header) || Encoding.ASCII.GetString(header, 4, 4) != "ftyp") return false;
                    date = ScanForDate(file, 0, file.Length);
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        public static int Patch(string path, DateTimeOffset target)
        {
            double totalSeconds = (target.ToUniversalTime() - Epoch).TotalSeconds;
            if (totalSeconds < 0 || totalSeconds > UInt64.MaxValue) throw new ExportException("视频日期超出 QuickTime 支持范围：" + path);
            ulong seconds = (ulong)Math.Floor(totalSeconds);
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                int changed = Scan(file, 0, file.Length, seconds);
                file.Flush(true);
                return changed;
            }
        }
    }

    public static class DateWriteEngine
    {
        private static readonly HashSet<string> Images = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".heic", ".heif", ".webp", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".rw2" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Videos = new HashSet<string>(new[] { ".mp4", ".mov", ".m4v", ".3gp", ".avi", ".wmv", ".mkv", ".mts", ".m2ts", ".mpg", ".mpeg", ".webm" }, StringComparer.OrdinalIgnoreCase);

        public static bool IsSupported(string path) { string e = Path.GetExtension(path); return Images.Contains(e) || Videos.Contains(e); }
        public static bool IsVideo(string path) { return Videos.Contains(Path.GetExtension(path)); }
        private static string Quote(string text) { return "\"" + text.Replace("\"", "\\\"") + "\""; }
        private static string NormalizePath(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant(); }
        private static bool ReadFully(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = stream.Read(buffer, offset, buffer.Length - offset);
                if (count <= 0) return false;
                offset += count;
            }
            return true;
        }
        private static string Md5(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (MD5 hash = MD5.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static DateTimeOffset? ParseDate(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            DateTimeOffset withOffset;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out withOffset)) return withOffset;
            string[] offsetFormats = { "yyyy:MM:dd HH:mm:sszzz", "yyyy-MM-dd HH:mm:sszzz", "yyyy:MM:dd HH:mm:ss.fffzzz", "yyyy-MM-dd HH:mm:ss.fffzzz" };
            if (DateTimeOffset.TryParseExact(value, offsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out withOffset)) return withOffset;
            string[] formats = { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fff" };
            DateTime local;
            if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
                return new DateTimeOffset(local, TimeSpan.FromHours(8));
            return null;
        }
        private static DateTimeOffset? ExistingDate(Dictionary<string, object> tags)
        {
            DateTimeOffset? earliest = null;
            foreach (KeyValuePair<string, object> tag in tags)
            {
                string key = tag.Key ?? "";
                bool isCapture = key.EndsWith(":DateTimeOriginal", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith(":CreateDate", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith(":MediaCreateDate", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith(":TrackCreateDate", StringComparison.OrdinalIgnoreCase) ||
                    key.EndsWith(":CreationDate", StringComparison.OrdinalIgnoreCase);
                if (!isCapture || key.IndexOf("File:", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                DateTimeOffset? date = ParseDate(Json.Text(tag.Value));
                if (date.HasValue && (!earliest.HasValue || date.Value < earliest.Value)) earliest = date;
            }
            return earliest;
        }
        private static ushort TiffUInt16(byte[] data, int offset, bool little)
        {
            if (offset < 0 || offset + 2 > data.Length) return 0;
            return little ? (ushort)(data[offset] | (data[offset + 1] << 8)) : (ushort)((data[offset] << 8) | data[offset + 1]);
        }
        private static uint TiffUInt32(byte[] data, int offset, bool little)
        {
            if (offset < 0 || offset + 4 > data.Length) return 0;
            if (little) return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }
        private static DateTimeOffset? TiffDateInIfd(byte[] data, int origin, bool little, uint ifdOffset, out uint exifIfd)
        {
            exifIfd = 0;
            long directoryOffset = origin + (long)ifdOffset;
            if (directoryOffset < 0 || directoryOffset + 2 > data.Length) return null;
            int count = TiffUInt16(data, (int)directoryOffset, little);
            DateTimeOffset? earliest = null;
            for (int i = 0; i < count; i++)
            {
                int entry = (int)directoryOffset + 2 + i * 12;
                if (entry + 12 > data.Length) break;
                ushort tag = TiffUInt16(data, entry, little);
                ushort type = TiffUInt16(data, entry + 2, little);
                uint length = TiffUInt32(data, entry + 4, little);
                if (tag == 0x8769) { exifIfd = TiffUInt32(data, entry + 8, little); continue; }
                if ((tag != 0x9003 && tag != 0x9004) || type != 2 || length == 0 || length > 64) continue;
                long textOffset = length <= 4 ? entry + 8 : origin + (long)TiffUInt32(data, entry + 8, little);
                if (textOffset < 0 || textOffset + length > data.Length) continue;
                string value = Encoding.ASCII.GetString(data, (int)textOffset, (int)length).TrimEnd('\0', ' ');
                DateTimeOffset? date = ParseDate(value);
                if (date.HasValue && (!earliest.HasValue || date.Value < earliest.Value)) earliest = date;
            }
            return earliest;
        }
        private static DateTimeOffset? TiffDate(byte[] data, int origin)
        {
            if (origin < 0 || origin + 8 > data.Length) return null;
            bool little = data[origin] == (byte)'I' && data[origin + 1] == (byte)'I';
            bool big = data[origin] == (byte)'M' && data[origin + 1] == (byte)'M';
            if (!little && !big || TiffUInt16(data, origin + 2, little) != 42) return null;
            uint exifIfd;
            TiffDateInIfd(data, origin, little, TiffUInt32(data, origin + 4, little), out exifIfd);
            if (exifIfd == 0) return null;
            uint unused;
            return TiffDateInIfd(data, origin, little, exifIfd, out unused);
        }
        private static DateTimeOffset? XmpDate(byte[] data)
        {
            string xml = Encoding.UTF8.GetString(data);
            string[] names = { "exif:DateTimeOriginal", "xmp:CreateDate", "photoshop:DateCreated" };
            DateTimeOffset? earliest = null;
            foreach (string name in names)
            {
                string[] starts = { name + "=\"", name + "='", "<" + name + ">" };
                foreach (string start in starts)
                {
                    int index = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    int valueStart = index + start.Length;
                    int valueEnd = start.EndsWith(">", StringComparison.Ordinal) ? xml.IndexOf("</", valueStart, StringComparison.Ordinal) : xml.IndexOf(start.EndsWith("'", StringComparison.Ordinal) ? "'" : "\"", valueStart, StringComparison.Ordinal);
                    if (valueEnd <= valueStart) continue;
                    DateTimeOffset? date = ParseDate(xml.Substring(valueStart, valueEnd - valueStart).Trim());
                    if (date.HasValue && (!earliest.HasValue || date.Value < earliest.Value)) earliest = date;
                }
            }
            return earliest;
        }
        private static bool TryReadJpegDate(string path, out DateTimeOffset? date)
        {
            date = null;
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (file.ReadByte() != 0xff || file.ReadByte() != 0xd8) return false;
                    while (file.Position + 4 <= file.Length)
                    {
                        int prefix = file.ReadByte();
                        if (prefix != 0xff) return false;
                        int marker; do { marker = file.ReadByte(); } while (marker == 0xff && file.Position < file.Length);
                        if (marker < 0 || marker == 0xd9 || marker == 0xda) return true;
                        if (marker == 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
                        int high = file.ReadByte(), low = file.ReadByte();
                        int length = (high << 8) | low;
                        if (high < 0 || low < 0 || length < 2 || file.Position + length - 2 > file.Length) return false;
                        if (marker != 0xe1) { file.Position += length - 2; continue; }
                        byte[] segment = new byte[length - 2];
                        if (!ReadFully(file, segment)) return false;
                        DateTimeOffset? candidate = null;
                        if (segment.Length >= 6 && Encoding.ASCII.GetString(segment, 0, 6) == "Exif\0\0") candidate = TiffDate(segment, 6);
                        else if (segment.Length >= 29 && Encoding.ASCII.GetString(segment, 0, 29) == "http://ns.adobe.com/xap/1.0/\0") candidate = XmpDate(segment);
                        if (candidate.HasValue && (!date.HasValue || candidate.Value < date.Value)) date = candidate;
                    }
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static bool TryReadTiffDate(string path, out DateTimeOffset? date)
        {
            date = null;
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int length = (int)Math.Min(file.Length, 262144);
                    if (length < 8) return false;
                    byte[] data = new byte[length];
                    if (!ReadFully(file, data)) return false;
                    bool tiff = (data[0] == (byte)'I' && data[1] == (byte)'I') || (data[0] == (byte)'M' && data[1] == (byte)'M');
                    if (!tiff) return false;
                    date = TiffDate(data, 0);
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static uint BigEndianUInt32(byte[] value, int offset)
        {
            if (offset < 0 || offset + 4 > value.Length) return 0;
            return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) | ((uint)value[offset + 2] << 8) | value[offset + 3];
        }
        private static uint LittleEndianUInt32(byte[] value, int offset)
        {
            if (offset < 0 || offset + 4 > value.Length) return 0;
            return (uint)(value[offset] | (value[offset + 1] << 8) | (value[offset + 2] << 16) | (value[offset + 3] << 24));
        }
        private static bool TryReadPngDate(string path, out DateTimeOffset? date)
        {
            date = null;
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] signature = new byte[8];
                    if (!ReadFully(file, signature) || signature[0] != 137 || Encoding.ASCII.GetString(signature, 1, 3) != "PNG") return false;
                    byte[] header = new byte[8];
                    while (file.Position + 12 <= file.Length)
                    {
                        if (!ReadFully(file, header)) return false;
                        uint length = BigEndianUInt32(header, 0);
                        string type = Encoding.ASCII.GetString(header, 4, 4);
                        if (length > file.Length - file.Position - 4) return false;
                        if ((type == "eXIf" || type == "iTXt" || type == "tEXt") && length <= 2097152)
                        {
                            byte[] data = new byte[(int)length];
                            if (!ReadFully(file, data)) return false;
                            DateTimeOffset? candidate = type == "eXIf" ? TiffDate(data, 0) : XmpDate(data);
                            if (candidate.HasValue && (!date.HasValue || candidate.Value < date.Value)) date = candidate;
                        }
                        else file.Position += length;
                        file.Position += 4;
                        if (type == "IEND") return true;
                    }
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static bool TryReadWebpDate(string path, out DateTimeOffset? date)
        {
            date = null;
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] signature = new byte[12];
                    if (!ReadFully(file, signature) || Encoding.ASCII.GetString(signature, 0, 4) != "RIFF" || Encoding.ASCII.GetString(signature, 8, 4) != "WEBP") return false;
                    byte[] header = new byte[8];
                    while (file.Position + 8 <= file.Length)
                    {
                        if (!ReadFully(file, header)) return false;
                        string type = Encoding.ASCII.GetString(header, 0, 4);
                        uint length = LittleEndianUInt32(header, 4);
                        if (length > file.Length - file.Position) return false;
                        if ((type == "EXIF" || type == "XMP ") && length <= 2097152)
                        {
                            byte[] data = new byte[(int)length];
                            if (!ReadFully(file, data)) return false;
                            DateTimeOffset? candidate = type == "EXIF" ? TiffDate(data, 0) : XmpDate(data);
                            if (candidate.HasValue && (!date.HasValue || candidate.Value < date.Value)) date = candidate;
                        }
                        else file.Position += length;
                        if ((length & 1) == 1) file.Position++;
                    }
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static bool TryReadFastDate(LocalFile file, out DateTimeOffset? date)
        {
            string extension = Path.GetExtension(file.Path);
            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadJpegDate(file.Path, out date)) return true;
                if (TryReadPngDate(file.Path, out date)) return true;
                return TryReadWebpDate(file.Path, out date);
            }
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return TryReadPngDate(file.Path, out date);
            if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)) return TryReadWebpDate(file.Path, out date);
            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) || extension.Equals(".3gp", StringComparison.OrdinalIgnoreCase)) return QuickTimeDatePatcher.TryReadCreatedDate(file.Path, out date);
            if (extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dng", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cr2", StringComparison.OrdinalIgnoreCase) || extension.Equals(".nef", StringComparison.OrdinalIgnoreCase) || extension.Equals(".arw", StringComparison.OrdinalIgnoreCase) || extension.Equals(".raf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rw2", StringComparison.OrdinalIgnoreCase)) return TryReadTiffDate(file.Path, out date);
            date = null;
            return false;
        }
        private static List<Photo> LoadPhotos(string jsonPath)
        {
            Dictionary<string, object> root;
            try { root = Json.Serializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(jsonPath, Encoding.UTF8)); }
            catch { throw new ExportException("photos.json 无法读取。请选择导出工具生成的 photos.json。 "); }
            IEnumerable raw = Json.Get(root, "photos") as IEnumerable;
            if (raw == null) throw new ExportException("这个 JSON 没有 photos 清单。请选择导出工具生成的 photos.json。 ");
            List<Photo> result = new List<Photo>();
            foreach (object item in raw)
            {
                Dictionary<string, object> record = Json.Object(item);
                if (record == null) continue;
                Photo photo = new Photo {
                    filename = Json.Text(Json.Get(record, "filename")), cloud_path = Json.Text(Json.Get(record, "cloud_path")),
                    md5_from_response = Json.Text(Json.Get(record, "md5_from_response")), album_time_china = Json.Text(Json.Get(record, "album_time_china"))
                };
                if (!String.IsNullOrWhiteSpace(photo.filename) && ParseDate(photo.album_time_china).HasValue) result.Add(photo);
            }
            return result;
        }
        private static List<LocalFile> FindFiles(string root, Action<string> log)
        {
            List<LocalFile> files = new List<LocalFile>();
            int seen = 0;
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!IsSupported(path)) continue;
                    seen++;
                    if (seen % 500 == 0) log("正在扫描本地目录：已发现 " + seen + " 个可写入文件。 ");
                    try { files.Add(new LocalFile { Path = Path.GetFullPath(path), Name = Path.GetFileName(path) }); }
                    catch (IOException) { log("跳过无法读取的文件：" + path); }
                    catch (UnauthorizedAccessException) { log("跳过无权限文件：" + path); }
                }
            }
            catch (UnauthorizedAccessException) { throw new ExportException("没有权限读取所选目录。请改选可访问的媒体目录。 "); }
            return files;
        }
        private static Dictionary<string, DateTimeOffset?> ReadExistingDates(string exifTool, IEnumerable<LocalFile> files, Action<string> log)
        {
            List<LocalFile> targets = files.ToList();
            Dictionary<string, DateTimeOffset?> dates = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
            List<LocalFile> compatibilityFiles = new List<LocalFile>();
            int fastFiles = 0;
            log("正在快速读取已有日期：0/" + targets.Count + "。 ");
            for (int i = 0; i < targets.Count; i++)
            {
                LocalFile file = targets[i];
                DateTimeOffset? existing;
                if (TryReadFastDate(file, out existing)) { dates[NormalizePath(file.Path)] = existing; fastFiles++; }
                else compatibilityFiles.Add(file);
                if ((i + 1) % 100 == 0 || i + 1 == targets.Count) log("正在快速读取已有日期：" + (i + 1) + "/" + targets.Count + "。 ");
            }
            if (compatibilityFiles.Count > 0) log("有 " + compatibilityFiles.Count + " 个文件无法快速确认已有日期；为避免覆盖风险，这些文件将跳过，不写入。 ");
            log("已有日期读取完成：快速读取 " + fastFiles + " 个文件。 ");
            return dates;
        }
        private static string Csv(string text)
        {
            text = text ?? "";
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        public static DatePlan Build(string jsonPath, string mediaRoot, string exifTool, Action<string> log)
        {
            List<Photo> cloud = LoadPhotos(jsonPath);
            if (!File.Exists(exifTool)) throw new ExportException("工具内置的 ExifTool 不完整，请检查程序目录中的 exiftool 文件夹。 ");
            log("清单中有 " + cloud.Count + " 条带一刻日期的记录。正在扫描本地目录……");
            List<LocalFile> local = FindFiles(mediaRoot, log);
            if (local.Count == 0) throw new ExportException("所选目录及其子目录中没有支持的照片或视频文件。 ");
            Dictionary<string, List<Photo>> byName = cloud.GroupBy(p => p.filename, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> localNames = local.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            DatePlan plan = new DatePlan { FilesScanned = local.Count };
            List<LocalFile> md5Candidates = new List<LocalFile>();
            foreach (LocalFile file in local)
            {
                List<Photo> candidates;
                if (byName.TryGetValue(file.Name, out candidates) && candidates.Count == 1 && localNames[file.Name] == 1)
                {
                    plan.Items.Add(new DatePlanItem { Local = file, Cloud = candidates[0], MatchMethod = "文件名", IsVideo = IsVideo(file.Path) }); plan.NameMatches++;
                }
                else if (candidates != null)
                {
                    md5Candidates.Add(file);
                }
                else plan.Unmatched++;
            }
            if (md5Candidates.Count > 0)
            {
                log("发现 " + md5Candidates.Count + " 个重名文件，正在校验 MD5。 ");
                Dictionary<string, List<Photo>> byMd5 = cloud.GroupBy(p => (p.md5_from_response ?? "").ToLowerInvariant()).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.ToList());
                for (int i = 0; i < md5Candidates.Count; i++)
                {
                    LocalFile file = md5Candidates[i];
                    if ((i + 1) % 25 == 0 || i + 1 == md5Candidates.Count) log("正在校验重名文件 MD5：" + (i + 1) + "/" + md5Candidates.Count + "。 ");
                    try { file.Md5 = Md5(file.Path); plan.Md5FilesChecked++; }
                    catch (IOException) { log("跳过无法读取的文件：" + file.Path); plan.Unmatched++; continue; }
                    catch (UnauthorizedAccessException) { log("跳过无权限文件：" + file.Path); plan.Unmatched++; continue; }
                    List<Photo> candidates;
                    if (byMd5.TryGetValue(file.Md5, out candidates) && candidates.Count == 1)
                    {
                        plan.Items.Add(new DatePlanItem { Local = file, Cloud = candidates[0], MatchMethod = "MD5", IsVideo = IsVideo(file.Path) }); plan.Md5Matches++;
                    }
                    else if (candidates != null || localNames[file.Name] > 1) plan.Ambiguous++;
                    else plan.Unmatched++;
                }
            }
            List<DatePlanItem> matched = plan.Items;
            Dictionary<string, DateTimeOffset?> metadata = ReadExistingDates(exifTool, matched.Select(item => item.Local), log);
            plan.Items = new List<DatePlanItem>();
            foreach (DatePlanItem item in matched)
            {
                DateTimeOffset? album = ParseDate(item.Cloud.album_time_china);
                DateTimeOffset? existing;
                if (!metadata.TryGetValue(NormalizePath(item.Local.Path), out existing)) { plan.Unsupported++; continue; }
                item.ExistingDate = existing;
                if (item.ExistingDate.HasValue) { plan.ExistingDateSkipped++; plan.FileTimeItems.Add(item); continue; }
                item.AlbumDate = album.Value;
                item.TargetDate = item.AlbumDate;
                plan.Items.Add(item);
                plan.FileTimeItems.Add(item);
            }
            return plan;
        }
        private static void Send(StreamWriter writer, string value) { writer.WriteLine(value); writer.Flush(); }
        private static string LocalDate(DateTimeOffset date) { return date.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture); }
        private static string LocalDateWithOffset(DateTimeOffset date) { return date.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy:MM:dd HH:mm:ss+08:00", CultureInfo.InvariantCulture); }
        private static void SetSystemDates(string path, DateTimeOffset date, Action<string> log)
        {
            try
            {
                DateTime local = date.ToOffset(TimeSpan.FromHours(8)).DateTime;
                File.SetCreationTime(path, local);
                File.SetLastWriteTime(path, local);
            }
            catch (Exception ex) { log("已写入媒体日期，但无法同步 Windows 文件日期：" + path + "（" + ex.Message + "）"); }
        }
        public static int ApplySystemDates(DatePlan plan, Action<string> log)
        {
            int updated = 0;
            for (int i = 0; i < plan.FileTimeItems.Count; i++)
            {
                DatePlanItem item = plan.FileTimeItems[i];
                DateTimeOffset? date = item.ExistingDate.HasValue ? item.ExistingDate : (item.DateWritten ? (DateTimeOffset?)item.TargetDate : null);
                if (!date.HasValue) continue;
                try
                {
                    DateTime local = date.Value.ToOffset(TimeSpan.FromHours(8)).DateTime;
                    File.SetCreationTime(item.Local.Path, local);
                    File.SetLastWriteTime(item.Local.Path, local);
                    updated++;
                }
                catch (Exception ex) { log("无法同步 Windows 文件日期：" + item.Local.Path + "（" + ex.Message + "）"); }
                if ((i + 1) % 500 == 0 || i + 1 == plan.FileTimeItems.Count) log("正在同步 Windows 文件日期：" + (i + 1) + "/" + plan.FileTimeItems.Count + "。 ");
            }
            return updated;
        }
        private static string ContentExtension(string path)
        {
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] header = new byte[12];
                    if (!ReadFully(file, header)) return null;
                    if (header[0] == 137 && Encoding.ASCII.GetString(header, 1, 3) == "PNG") return ".png";
                    if (Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP") return ".webp";
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
        private static string PrepareExifPath(string originalPath, out string temporaryPath)
        {
            temporaryPath = null;
            string extension = ContentExtension(originalPath);
            if (String.IsNullOrEmpty(extension) || extension.Equals(Path.GetExtension(originalPath), StringComparison.OrdinalIgnoreCase)) return originalPath;
            temporaryPath = originalPath + ".yike-date-" + Guid.NewGuid().ToString("N") + extension;
            File.Move(originalPath, temporaryPath);
            return temporaryPath;
        }
        private static void RestoreOriginalPath(string originalPath, string temporaryPath, Action<string> log)
        {
            if (String.IsNullOrEmpty(temporaryPath) || !File.Exists(temporaryPath)) return;
            try { File.Move(temporaryPath, originalPath); }
            catch (Exception ex) { log("临时文件无法恢复原始名称：" + temporaryPath + "（" + ex.Message + "）"); }
        }
        private static void LittleUInt16(byte[] value, int offset, ushort number) { value[offset] = (byte)number; value[offset + 1] = (byte)(number >> 8); }
        private static void LittleUInt32(byte[] value, int offset, uint number) { value[offset] = (byte)number; value[offset + 1] = (byte)(number >> 8); value[offset + 2] = (byte)(number >> 16); value[offset + 3] = (byte)(number >> 24); }
        private static void ExifEntry(byte[] value, int offset, ushort tag, ushort type, uint count, uint dataOffset)
        {
            LittleUInt16(value, offset, tag); LittleUInt16(value, offset + 2, type); LittleUInt32(value, offset + 4, count); LittleUInt32(value, offset + 8, dataOffset);
        }
        private static byte[] CleanExif(DateTimeOffset date)
        {
            byte[] value = new byte[153];
            Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(value, 0);
            int tiff = 6;
            value[tiff] = (byte)'I'; value[tiff + 1] = (byte)'I'; LittleUInt16(value, tiff + 2, 42); LittleUInt32(value, tiff + 4, 8);
            LittleUInt16(value, tiff + 8, 2);
            ExifEntry(value, tiff + 10, 0x0132, 2, 20, 80);
            ExifEntry(value, tiff + 22, 0x8769, 4, 1, 38);
            LittleUInt32(value, tiff + 34, 0);
            LittleUInt16(value, tiff + 38, 3);
            ExifEntry(value, tiff + 40, 0x9003, 2, 20, 100);
            ExifEntry(value, tiff + 52, 0x9004, 2, 20, 120);
            ExifEntry(value, tiff + 64, 0x9011, 2, 7, 140);
            LittleUInt32(value, tiff + 76, 0);
            string local = LocalDate(date);
            Encoding.ASCII.GetBytes(local + "\0").CopyTo(value, tiff + 80);
            Encoding.ASCII.GetBytes(local + "\0").CopyTo(value, tiff + 100);
            Encoding.ASCII.GetBytes(local + "\0").CopyTo(value, tiff + 120);
            Encoding.ASCII.GetBytes("+08:00\0").CopyTo(value, tiff + 140);
            return value;
        }
        private static bool IsExifSegment(byte[] value) { return value.Length >= 6 && Encoding.ASCII.GetString(value, 0, 6) == "Exif\0\0"; }
        private static void WriteJpegSegment(Stream output, int marker, byte[] data)
        {
            output.WriteByte(0xff); output.WriteByte((byte)marker);
            int length = data.Length + 2;
            output.WriteByte((byte)(length >> 8)); output.WriteByte((byte)length);
            output.Write(data, 0, data.Length);
        }
        private static bool RewriteBrokenJpegExif(string path, DateTimeOffset date)
        {
            string temporary = path + ".yike-date-rebuild-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (input.ReadByte() != 0xff || input.ReadByte() != 0xd8) return false;
                    output.WriteByte(0xff); output.WriteByte(0xd8); WriteJpegSegment(output, 0xe1, CleanExif(date));
                    while (input.Position < input.Length)
                    {
                        int prefix = input.ReadByte();
                        if (prefix != 0xff) return false;
                        int marker; do { marker = input.ReadByte(); } while (marker == 0xff && input.Position < input.Length);
                        if (marker < 0) return false;
                        if (marker == 0xd9 || marker == 0x01 || marker >= 0xd0 && marker <= 0xd7)
                        {
                            output.WriteByte(0xff); output.WriteByte((byte)marker);
                            if (marker == 0xd9) break;
                            continue;
                        }
                        int high = input.ReadByte(), low = input.ReadByte();
                        int length = (high << 8) | low;
                        if (high < 0 || low < 0 || length < 2) return false;
                        byte[] data = new byte[length - 2];
                        if (!ReadFully(input, data)) return false;
                        if (!(marker == 0xe1 && IsExifSegment(data))) WriteJpegSegment(output, marker, data);
                        if (marker == 0xda) { input.CopyTo(output); break; }
                    }
                    output.Flush(true);
                }
                File.Replace(temporary, path, null, true);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void WriteOne(StreamWriter input, string path, DatePlanItem item, int number)
        {
            string local = LocalDate(item.TargetDate);
            string withOffset = LocalDateWithOffset(item.TargetDate);
            // The packaged Windows ExifTool receives standard input in the active Windows code page.
            Send(input, "-charset"); Send(input, "filename=cp936");
            Send(input, "-overwrite_original");
            Send(input, "-EXIF:DateTimeOriginal=" + local); Send(input, "-EXIF:CreateDate=" + local); Send(input, "-EXIF:ModifyDate=" + local);
            Send(input, "-EXIF:OffsetTimeOriginal=+08:00"); Send(input, "-XMP-exif:DateTimeOriginal=" + withOffset);
            Send(input, "-XMP-xmp:CreateDate=" + withOffset); Send(input, "-XMP-xmp:ModifyDate=" + withOffset);
            Send(input, "-FileCreateDate=" + local); Send(input, "-FileModifyDate=" + local);
            Send(input, path); Send(input, "-execute" + number);
        }
        public static int Write(DatePlan plan, string exifTool, Action<string> log)
        {
            int updated = 0;
            List<DatePlanItem> videos = plan.Items.Where(item => item.IsVideo).ToList();
            List<DatePlanItem> images = plan.Items.Where(item => !item.IsVideo).ToList();
            for (int i = 0; i < videos.Count; i++)
            {
                DatePlanItem item = videos[i];
                try
                {
                    int fields = QuickTimeDatePatcher.Patch(item.Local.Path, item.TargetDate);
                    if (fields > 0) { updated++; item.DateWritten = true; SetSystemDates(item.Local.Path, item.TargetDate, log); }
                    else log("未写入视频：未找到可修改的 QuickTime 时间字段：" + item.Local.Path);
                }
                catch (Exception ex) { log("未写入视频：" + item.Local.Path + "（" + ex.Message + "）"); }
                if ((i + 1) % 25 == 0 || i + 1 == videos.Count) log("正在快速写入视频：" + (i + 1) + "/" + videos.Count + "。 ");
            }
            if (images.Count == 0) return updated;
            ProcessStartInfo start = new ProcessStartInfo {
                FileName = exifTool, Arguments = "-stay_open True -@ -", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(start))
            {
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { };
                process.BeginErrorReadLine();
                StreamWriter input = process.StandardInput;
                for (int i = 0; i < images.Count; i++)
                {
                    DatePlanItem item = images[i];
                    string temporaryPath = null;
                    try
                    {
                        string writePath = PrepareExifPath(item.Local.Path, out temporaryPath);
                        WriteOne(input, writePath, item, i + 1);
                        string ready = "{ready" + (i + 1) + "}";
                        bool ok = false; string line;
                        while ((line = process.StandardOutput.ReadLine()) != null)
                        {
                            if (line.IndexOf("1 image files updated", StringComparison.OrdinalIgnoreCase) >= 0) ok = true;
                            if (line == ready) break;
                        }
                        if (ok) { updated++; item.DateWritten = true; RestoreOriginalPath(item.Local.Path, temporaryPath, log); temporaryPath = null; SetSystemDates(item.Local.Path, item.TargetDate, log); }
                        else if (String.IsNullOrEmpty(temporaryPath) && RewriteBrokenJpegExif(item.Local.Path, item.TargetDate)) { updated++; item.DateWritten = true; SetSystemDates(item.Local.Path, item.TargetDate, log); }
                        else log("未写入：" + item.Local.Path);
                    }
                    catch (Exception ex) { log("未写入：" + item.Local.Path + "（" + ex.Message + "）"); }
                    finally { RestoreOriginalPath(item.Local.Path, temporaryPath, log); }
                    if ((i + 1) % 25 == 0 || i + 1 == images.Count) log("正在写入图片：" + (i + 1) + "/" + images.Count + "。 ");
                }
                Send(input, "-stay_open"); Send(input, "False");
                process.WaitForExit();
            }
            return updated;
        }
    }

    public sealed class DateWriteForm : Form
    {
        private TextBox jsonBox, mediaBox, logBox;
        private Button processButton;
        private bool busy;
        public DateWriteForm()
        {
            Text = "本地媒体日期写入"; Size = new Size(900, 640); MinimumSize = new Size(820, 560); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 10F); BackColor = Color.FromArgb(248, 250, 253);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            float[] rows = { 45, 45, 28, 38, 28, 38, 46, 28 }; foreach (float row in rows) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, row)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "本地媒体日期写入", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(new Label { Text = "依据一刻导出清单补写媒体元数据。仅处理未检测到拍摄日期的文件，已有日期的文件保持不变。", Dock = DockStyle.Fill }, 0, 1);
            layout.Controls.Add(new Label { Text = "一刻清单文件（photos.json）", Dock = DockStyle.Fill }, 0, 2);
            jsonBox = new TextBox { Dock = DockStyle.Fill }; layout.Controls.Add(BrowseRow(jsonBox, "选择 photos.json", false), 0, 3);
            layout.Controls.Add(new Label { Text = "媒体根目录（包含子目录）", Dock = DockStyle.Fill }, 0, 4);
            mediaBox = new TextBox { Dock = DockStyle.Fill }; layout.Controls.Add(BrowseRow(mediaBox, "选择媒体目录", true), 0, 5);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) }; processButton = new Button { Text = "开始处理", Width = 145, Height = 32 }; buttons.Controls.Add(processButton); layout.Controls.Add(buttons, 0, 6);
            layout.Controls.Add(new Label { Text = "处理日志", Dock = DockStyle.Fill }, 0, 7);
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9F) }; layout.Controls.Add(logBox, 0, 8);
            processButton.Click += async delegate { await ProcessDirect(); };
            AddLog("就绪。开始处理后将自动匹配文件并写入日期。 ");
        }
        private Control BrowseRow(TextBox box, string title, bool folder)
        {
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); Button button = new Button { Text = "选择…", Dock = DockStyle.Fill };
            button.Click += delegate { if (folder) { using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = title }) if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; } else { using (OpenFileDialog dialog = new OpenFileDialog { Title = title, Filter = "一刻照片清单|photos.json|JSON 文件|*.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; } };
            row.Controls.Add(box, 0, 0); row.Controls.Add(button, 1, 0); return row;
        }
        private string ExifToolPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exiftool", "ExifTool.exe"); } }
        private void AddLog(string message) { if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), message); return; } logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine); }
        private void SetBusy(bool value) { busy = value; processButton.Enabled = !value; jsonBox.Enabled = mediaBox.Enabled = !value; }
        private async Task ProcessDirect()
        {
            if (busy) return; string json = jsonBox.Text.Trim(), media = mediaBox.Text.Trim();
            if (!File.Exists(json) || !Directory.Exists(media)) { MessageBox.Show(this, "请选择有效的 photos.json 和媒体目录。", "检查输入", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SetBusy(true); AddLog("开始扫描和匹配；文件较多时需要一些时间。 ");
            try
            {
                DatePlan plan = await Task.Run(() => DateWriteEngine.Build(json, media, ExifToolPath, AddLog));
                AddLog("匹配完成：文件名 " + plan.NameMatches + "，MD5 " + plan.Md5Matches + "（校验 " + plan.Md5FilesChecked + " 个重名文件），已有日期跳过 " + plan.ExistingDateSkipped + "，无法确认日期跳过 " + plan.Unsupported + "，未匹配 " + plan.Unmatched + "，歧义 " + plan.Ambiguous + "。 ");
                int count = 0;
                if (plan.Items.Count > 0)
                {
                    AddLog("开始写入 " + plan.Items.Count + " 个文件的日期元数据。 ");
                    count = await Task.Run(() => DateWriteEngine.Write(plan, ExifToolPath, AddLog));
                    AddLog("媒体日期写入完成：" + count + "/" + plan.Items.Count + " 个文件。 ");
                }
                int fileDates = await Task.Run(() => DateWriteEngine.ApplySystemDates(plan, AddLog));
                AddLog("Windows 文件日期同步完成：" + fileDates + "/" + plan.FileTimeItems.Count + " 个文件。 ");
            }
            catch (ExportException ex) { AddLog(ex.Message); }
            catch (Exception ex) { AddLog("处理失败：" + ex.Message); }
            finally { SetBusy(false); }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
