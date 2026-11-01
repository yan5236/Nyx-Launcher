using System.Net.Sockets;
using System.Text;

namespace NyxLauncher.Services;

/// <summary>保留诊断必需的异常类型、错误码与调用栈；不记录可能包含认证正文或令牌的异常消息。</summary>
internal static class NetworkFailureDetails
{
    /// <summary>展开内部异常并输出安全的结构化诊断；不读取 Data、请求头或请求正文。</summary>
    internal static string Format(Exception error)
    {
        var text = new StringBuilder();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            text.AppendLine($"{current.GetType().FullName} HResult=0x{current.HResult:X8}");
            if (current is SocketException socket) text.AppendLine($"SocketError={socket.SocketErrorCode} NativeError={socket.NativeErrorCode}");
            if (current is HttpRequestException http) text.AppendLine($"HttpRequestError={http.HttpRequestError} Status={http.StatusCode}");
            text.AppendLine(current.StackTrace);
        }
        return text.ToString();
    }
}
