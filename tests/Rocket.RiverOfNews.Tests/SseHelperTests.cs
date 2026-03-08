using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Rocket.RiverOfNews.Datastar;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class SseHelperTests
{
[Test]
public async Task PatchElementsAsync_WithCarriageReturns_NormalizesMultilineOutput()
{
DefaultHttpContext httpContext = new();
await using MemoryStream responseBody = new();
httpContext.Response.Body = responseBody;

SseHelper helper = new(httpContext.Response);
await helper.StartAsync(CancellationToken.None);
await helper.PatchElementsAsync("<div>\r\n  <span>Line</span>\r\n</div>", "#target", "inner", CancellationToken.None);

string body = await ReadBodyAsync(responseBody);
await Assert.That(body.Contains("data: selector #target")).IsTrue();
await Assert.That(body.Contains("data: elements <div>\n")).IsTrue();
await Assert.That(body.Contains("data: elements   <span>Line</span>\n")).IsTrue();
await Assert.That(body.Contains('\r')).IsFalse();
}

[Test]
public async Task ReadSignalsAsync_WithQueryString_UsesDatastarPayload()
{
DefaultHttpContext httpContext = new();
httpContext.Request.QueryString = new QueryString("?datastar=%7B%22selectedFeedIds%22%3A%22feed-1%22%7D");

Dictionary<string, JsonElement> signals = await httpContext.Request.ReadSignalsAsync(CancellationToken.None);
await Assert.That(signals["selectedFeedIds"].GetString()).IsEqualTo("feed-1");
}

private static async Task<string> ReadBodyAsync(MemoryStream stream)
{
stream.Position = 0;
using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
return await reader.ReadToEndAsync();
}
}
