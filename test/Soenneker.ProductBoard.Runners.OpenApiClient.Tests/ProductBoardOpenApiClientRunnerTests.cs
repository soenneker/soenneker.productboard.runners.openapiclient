using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.ProductBoard.Runners.OpenApiClient.Tests;

[Collection("Collection")]
public sealed class ProductBoardOpenApiClientRunnerTests : FixturedUnitTest
{
    public ProductBoardOpenApiClientRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
