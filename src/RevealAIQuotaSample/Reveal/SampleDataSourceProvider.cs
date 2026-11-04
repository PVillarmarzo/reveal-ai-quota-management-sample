using Reveal.Sdk;
using Reveal.Sdk.Data;
using Reveal.Sdk.Data.Rest;

namespace RevealAIQuotaSample.Reveal;

/// <summary>
/// Resolves the one datasource listed in Reveal/Metadata/catalog.json ("SampleExcel") to a public
/// sample Excel file. Replace this with your own datasources.
/// </summary>
public sealed class SampleDataSourceProvider : IRVDataSourceProvider
{
    public const string SampleDataSourceId = "SampleExcel";
    private const string SampleDataUrl = "https://download.infragistics.com/reveal/sampledata/SamplesIA.xlsx";

    public Task<RVDataSourceItem> ChangeDataSourceItemAsync(IRVUserContext userContext, string dashboardId, RVDataSourceItem dataSourceItem)
    {
        Configure(dataSourceItem.DataSource);
        return Task.FromResult(dataSourceItem);
    }

    public Task<RVDashboardDataSource> ChangeDataSourceAsync(IRVUserContext userContext, RVDashboardDataSource dataSource)
    {
        Configure(dataSource);
        return Task.FromResult(dataSource);
    }

    private static void Configure(RVDashboardDataSource dataSource)
    {
        if (dataSource.Id == SampleDataSourceId && dataSource is RVWebResourceDataSource web)
        {
            web.UseAnonymousAuthentication = true;
            web.Url = SampleDataUrl;
        }
    }
}
