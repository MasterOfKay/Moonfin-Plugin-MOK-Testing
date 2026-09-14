using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    // Any signed-in user, not just an admin, since crash reports come from whoever hit the bug.
    [Route("/Moonfin/ClientLog/Document", "POST")]
    [Authenticated]
    public class UploadClientLogRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }
}
