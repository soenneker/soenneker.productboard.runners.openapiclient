using Soenneker.Tests.HostedUnit;

namespace Soenneker.ProductBoard.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class ProductBoardOpenApiClientRunnerTests : HostedUnitTest
{
    public ProductBoardOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
