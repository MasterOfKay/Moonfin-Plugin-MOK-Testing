using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Takes diagnostic and crash reports from Moonfin clients and writes them into the server's
    /// log folder, where they appear in Dashboard > Logs. Emby has no client log endpoint of its
    /// own, so this is what the app posts to instead.
    /// </summary>
    public class ClientLogService : IService, IRequiresRequest, IHasResultFactory
    {
        private const int MaxDocumentBytes = 1024 * 1024;

        private const int MaxUploadsPerHour = 10;

        /// <summary>Per-user upload budget, so a client stuck retrying cannot fill the log folder.</summary>
        private static readonly ConcurrentDictionary<Guid, UploadBudget> Budgets =
            new ConcurrentDictionary<Guid, UploadBudget>();

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public ClientLogService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public async Task<object> Post(UploadClientLogRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableClientLogUpload != true)
                return Json(503, new { Error = "Client log upload is disabled" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null)
                return Json(401, new { Error = "User not authenticated" });

            if (!TryClaimUploadSlot(userId.Value))
                return Json(429, new { Error = "Too many reports uploaded recently. Try again later." });

            byte[] content;
            try
            {
                content = await ReadCappedAsync(request.RequestStream).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return Json(413, new { Error = "Report is larger than " + MaxDocumentBytes + " bytes" });
            }

            if (content.Length == 0)
                return Json(400, new { Error = "No body provided." });

            var logDirectory = Plugin.Instance?.LogDirectoryPath;
            if (string.IsNullOrWhiteSpace(logDirectory))
                return Json(500, new { Error = "Log directory is unavailable" });

            var client = "client";
            var version = "0";
            try
            {
                var auth = _authContext.GetAuthorizationInfo(Request);
                client = Sanitize(auth?.Client, "client");
                version = Sanitize(auth?.Version, "0");
            }
            catch
            {
                // An unreadable auth header only costs a less descriptive file name.
            }

            try
            {
                var fileName = await WriteReportAsync(logDirectory!, client, version, content).ConfigureAwait(false);
                if (fileName == null)
                    return Json(500, new { Error = "Could not write the report" });

                return Json(new { FileName = fileName });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Json(500, new { Error = "Could not write the report: " + ex.Message });
            }
        }

        /// <summary>
        /// A crash flush uploads its queue back to back, so the timestamp carries milliseconds and
        /// CreateNew plus a counter settles anything that still lands in the same one.
        /// </summary>
        private static async Task<string?> WriteReportAsync(string logDirectory, string client, string version, byte[] content)
        {
            var resolvedDirectory = Path.GetFullPath(logDirectory).TrimEnd(Path.DirectorySeparatorChar);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var fileName = BuildFileName(client, version, DateTime.UtcNow, attempt);
                var target = Path.Combine(logDirectory, fileName);

                // The name never comes from the request, and this confirms it stayed put.
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), resolvedDirectory, StringComparison.Ordinal))
                    return null;

                try
                {
                    using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    {
                        await file.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
                    }
                    return fileName;
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Same client, same millisecond. Try the next counter.
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the body, refusing anything past the cap instead of buffering it all first.
        /// Reads asynchronously: Emby's request stream forbids synchronous reads.
        /// </summary>
        private static async Task<byte[]> ReadCappedAsync(Stream? stream)
        {
            if (stream == null) return Array.Empty<byte>();

            using (var buffered = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    if (buffered.Length + read > MaxDocumentBytes)
                        throw new InvalidDataException("Report exceeds the size cap");

                    buffered.Write(buffer, 0, read);
                }
                return buffered.ToArray();
            }
        }

        /// <summary>
        /// Keeps only characters that are safe in a file name, so nothing a client sends can steer
        /// where the report lands. Falls back when nothing usable is left.
        /// </summary>
        internal static string Sanitize(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            var builder = new StringBuilder(value!.Length);
            foreach (var c in value)
            {
                var keep = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_';

                if (keep)
                {
                    builder.Append(c);
                }
                else if (char.IsWhiteSpace(c) && builder.Length > 0 && builder[builder.Length - 1] != '-')
                {
                    // Client names carry spaces, as in "Moonfin for Android TV", and dropping them
                    // outright would leave MoonfinforAndroidTV in the file name.
                    builder.Append('-');
                }

                if (builder.Length >= 32) break;
            }

            var cleaned = builder.ToString().Trim('-');
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        /// <summary>Ends in .txt to match what Emby writes its own logs as, so the report lists beside them.</summary>
        internal static string BuildFileName(string client, string version, DateTime utcNow, int attempt)
        {
            var suffix = attempt == 0 ? string.Empty : "_" + attempt.ToString(CultureInfo.InvariantCulture);
            return "upload_" + client + "_" + version + "_"
                + utcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)
                + suffix + ".txt";
        }

        private static bool TryClaimUploadSlot(Guid userId)
        {
            var now = DateTime.UtcNow;
            var budget = Budgets.GetOrAdd(userId, _ => new UploadBudget { WindowStart = now });

            lock (budget)
            {
                if (now - budget.WindowStart >= TimeSpan.FromHours(1))
                {
                    budget.WindowStart = now;
                    budget.Count = 0;
                }

                if (budget.Count >= MaxUploadsPerHour) return false;

                budget.Count++;
                return true;
            }
        }

        private sealed class UploadBudget
        {
            public DateTime WindowStart;
            public int Count;
        }
    }
}
