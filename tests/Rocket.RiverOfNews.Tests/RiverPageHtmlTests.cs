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
		IResult result = DatastarApi.GetRiverPage();
		string body = await ExecuteResultAsync(result);

		await Assert.That(body.Contains("max-h-56 w-full rounded object-cover")).IsFalse();
	}

	[Test]
	public async Task GetRiverItemPage_IncludesImageElementWithBinding()
	{
		IResult result = DatastarApi.GetRiverItemPage("item-1");
		string body = await ExecuteResultAsync(result);

		await Assert.That(body.Contains("data-show=\"$imageUrl\"")).IsTrue();
		await Assert.That(body.Contains("data-attr:src=\"$imageUrl\"")).IsTrue();
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
