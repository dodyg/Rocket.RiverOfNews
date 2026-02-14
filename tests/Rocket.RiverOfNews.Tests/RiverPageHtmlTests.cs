using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rocket.RiverOfNews.Api;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class RiverPageHtmlTests
{
	[Test]
	public async Task GetRiverPage_UsesIntrinsicImageSizingClasses()
	{
		IResult result = MvpApi.GetRiverPage();
		string body = await ExecuteResultAsync(result);

		await Assert.That(body.Contains("class=\"mb-3 h-auto w-auto max-w-full rounded\"")).IsTrue();
		await Assert.That(body.Contains("max-h-56 w-full rounded object-cover")).IsFalse();
	}

	[Test]
	public async Task GetRiverItemPage_IncludesImageElementAndBinding()
	{
		IResult result = MvpApi.GetRiverItemPage("item-1");
		string body = await ExecuteResultAsync(result);

		await Assert.That(body.Contains("id=\"itemImage\" class=\"mb-4 hidden h-auto w-auto max-w-full rounded\"")).IsTrue();
		await Assert.That(body.Contains("itemImage.src = payload.imageUrl;")).IsTrue();
	}

	private static async Task<string> ExecuteResultAsync(IResult result)
	{
		DefaultHttpContext httpContext = new();
		ServiceCollection services = new();
		services.AddLogging();
		services.AddOptions();
		httpContext.RequestServices = services.BuildServiceProvider();
		await using MemoryStream bodyStream = new();
		httpContext.Response.Body = bodyStream;

		await result.ExecuteAsync(httpContext);
		bodyStream.Position = 0;
		using StreamReader reader = new(bodyStream);
		return await reader.ReadToEndAsync();
	}
}
