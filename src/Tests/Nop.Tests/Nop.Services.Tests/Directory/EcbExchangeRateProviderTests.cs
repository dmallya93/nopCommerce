using System.Net;
using System.Text;
using AwesomeAssertions;
using Moq;
using Nop.Core;
using Nop.Core.Domain.Customers;
using Nop.Core.Http;
using Nop.Plugin.ExchangeRate.EcbExchange;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using NUnit.Framework;

namespace Nop.Tests.Nop.Services.Tests.Directory;

/// <summary>
/// Pure unit tests (no DI/SQLite) that codify the legacy nopCommerce 3.90 behavioral contract
/// for the ECB exchange rate provider's cross-rate math and failure handling, against the current
/// implementation (<see cref="EcbExchangeRateProvider"/>).
/// </summary>
[TestFixture]
public class EcbExchangeRateProviderTests
{
    private const string ValidEcbXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<gesmes:Envelope xmlns:gesmes=""http://www.gesmes.org/xml/2002-08-01"" xmlns=""http://www.ecb.int/vocabulary/2002-08-01/eurofxref"">
  <gesmes:subject>Reference rates</gesmes:subject>
  <Cube>
    <Cube time=""2026-10-13"">
      <Cube currency=""USD"" rate=""1.1000""/>
      <Cube currency=""RUB"" rate=""99.0000""/>
      <Cube currency=""GBP"" rate=""0.8500""/>
    </Cube>
  </Cube>
</gesmes:Envelope>";

    private const string EcbXmlWithInvalidTimeAttribute = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<gesmes:Envelope xmlns:gesmes=""http://www.gesmes.org/xml/2002-08-01"" xmlns=""http://www.ecb.int/vocabulary/2002-08-01/eurofxref"">
  <gesmes:subject>Reference rates</gesmes:subject>
  <Cube>
    <Cube time=""not-a-date"">
      <Cube currency=""USD"" rate=""1.1000""/>
    </Cube>
  </Cube>
</gesmes:Envelope>";

    private EcbExchangeRateSettings _settings;
    private Mock<IHttpClientFactory> _httpClientFactoryMock;
    private Mock<ILocalizationService> _localizationServiceMock;
    private Mock<ILogger> _loggerMock;
    private Mock<ISettingService> _settingServiceMock;

    private HttpClient _httpClient;

    [SetUp]
    public void SetUp()
    {
        _settings = new EcbExchangeRateSettings { EcbLink = "https://example.test/eurofxref-daily.xml" };
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _localizationServiceMock = new Mock<ILocalizationService>();
        _localizationServiceMock.Setup(s => s.GetResourceAsync(It.IsAny<string>()))
            .ReturnsAsync("Unsupported currency by ECB");
        _loggerMock = new Mock<ILogger>();
        _settingServiceMock = new Mock<ISettingService>();
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    private EcbExchangeRateProvider CreateProvider(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler);
        _httpClientFactoryMock.Setup(f => f.CreateClient(NopHttpDefaults.DefaultHttpClient)).Returns(_httpClient);

        return new EcbExchangeRateProvider(_settings, _httpClientFactoryMock.Object,
            _localizationServiceMock.Object, _loggerMock.Object, _settingServiceMock.Object);
    }

    // Contract #8: The ECB provider computes non-EUR cross rates as rate-to-EUR / target-to-EUR,
    // rounded to 4 decimal places (Math.Round(x, 4)), with EUR itself always returned at rate 1.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_Eur_ShouldReturnEurAtRateOneAndAllFetchedRates()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, ValidEcbXml));

        var result = await provider.GetCurrencyLiveRatesAsync("eur");

        result.Should().NotBeNull();
        result.Should().ContainSingle(r => r.CurrencyCode == "EUR" && r.Rate == 1m);
        result.Should().Contain(r => r.CurrencyCode == "USD" && r.Rate == 1.1000m);
        result.Should().Contain(r => r.CurrencyCode == "RUB" && r.Rate == 99.0000m);
        result.Should().Contain(r => r.CurrencyCode == "GBP" && r.Rate == 0.8500m);
        result.Should().HaveCount(4);
    }

    // Contract #8: EUR lookup is case-insensitive ("eur" is checked via
    // StringComparison.InvariantCultureIgnoreCase against the requested code).
    [Test]
    public async Task GetCurrencyLiveRatesAsync_EurMixedCase_ShouldStillReturnEuroBaseRates()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, ValidEcbXml));

        var result = await provider.GetCurrencyLiveRatesAsync("EuR");

        result.Should().Contain(r => r.CurrencyCode == "EUR" && r.Rate == 1m);
    }

    // Contract #8: non-EUR cross rates are computed as rate-to-EUR / target-to-EUR, rounded to 4 decimals.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_NonEurBase_ShouldComputeCrossRatesRoundedToFourDecimals()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, ValidEcbXml));

        // USD-to-EUR is 1.1000; cross rates are expressed relative to USD
        var result = await provider.GetCurrencyLiveRatesAsync("USD");

        result.Should().NotBeNull();

        var eur = result.Single(r => r.CurrencyCode == "EUR");
        eur.Rate.Should().Be(Math.Round(1m / 1.1000m, 4));

        var usd = result.Single(r => r.CurrencyCode == "USD");
        usd.Rate.Should().Be(Math.Round(1.1000m / 1.1000m, 4));
        usd.Rate.Should().Be(1m);

        var rub = result.Single(r => r.CurrencyCode == "RUB");
        rub.Rate.Should().Be(Math.Round(99.0000m / 1.1000m, 4));

        var gbp = result.Single(r => r.CurrencyCode == "GBP");
        gbp.Rate.Should().Be(Math.Round(0.8500m / 1.1000m, 4));
    }

    // Contract #8: the ECB feed's <Cube time="yyyy-MM-dd"> attribute is parsed (DateTime.TryParseExact)
    // and stamped onto every rate returned for that fetch, rather than being ignored or re-stamped per rate.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_ShouldParseFeedDateAndStampItOnEachRate()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, ValidEcbXml));

        var result = await provider.GetCurrencyLiveRatesAsync("eur");

        var expectedDate = DateTime.ParseExact("2026-10-13", "yyyy-MM-dd", null);
        result.Single(r => r.CurrencyCode == "USD").UpdatedOn.Should().Be(expectedDate);
        result.Single(r => r.CurrencyCode == "RUB").UpdatedOn.Should().Be(expectedDate);
        result.Single(r => r.CurrencyCode == "GBP").UpdatedOn.Should().Be(expectedDate);
    }

    // Contract #8 (fallback half): when the feed's time attribute is missing or fails to parse as
    // "yyyy-MM-dd", the provider falls back to DateTime.UtcNow rather than throwing or leaving it unset.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_ShouldFallBackToUtcNow_WhenFeedDateIsUnparseable()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, EcbXmlWithInvalidTimeAttribute));

        var before = DateTime.UtcNow.AddSeconds(-5);
        var result = await provider.GetCurrencyLiveRatesAsync("eur");
        var after = DateTime.UtcNow.AddSeconds(5);

        var usd = result.Single(r => r.CurrencyCode == "USD");
        usd.UpdatedOn.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // Contract #8/#9: an unmatched (unsupported-by-ECB) currency code throws NopException rather than
    // silently returning an empty/partial result.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_UnknownCurrencyCode_ShouldThrowNopException()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, ValidEcbXml));

        Func<Task> act = () => provider.GetCurrencyLiveRatesAsync("ZZZ");

        await act.Should().ThrowAsync<NopException>();
    }

    // Contract #9: provider-level failures (HTTP/XML errors) are caught and logged via
    // ILogger.ErrorAsync without throwing.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_HttpFailure_ShouldBeCaughtAndLogged_AndNotThrow()
    {
        var provider = CreateProvider(new ThrowingHandler());

        Func<Task> act = () => provider.GetCurrencyLiveRatesAsync("eur");

        await act.Should().NotThrowAsync();
        _loggerMock.Verify(l => l.ErrorAsync("ECB exchange rate provider", It.IsAny<Exception>(), It.IsAny<Customer>()),
            Times.Once);
    }

    // Contract #9: after a provider-level (HTTP) failure, EUR-only results (the seeded EUR @ rate 1)
    // are still returned rather than the whole call failing.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_HttpFailure_ShouldStillReturnEurOnlyResult()
    {
        var provider = CreateProvider(new ThrowingHandler());

        var result = await provider.GetCurrencyLiveRatesAsync("eur");

        result.Should().NotBeNull();
        result.Should().ContainSingle();
        result.Single().CurrencyCode.Should().Be("EUR");
        result.Single().Rate.Should().Be(1m);
    }

    // Contract #9: malformed/unparseable XML from the feed is also a provider-level failure that is
    // caught and logged rather than propagated.
    [Test]
    public async Task GetCurrencyLiveRatesAsync_MalformedXml_ShouldBeCaughtAndLogged_AndNotThrow()
    {
        var provider = CreateProvider(new StaticResponseHandler(HttpStatusCode.OK, "not-valid-xml<<<"));

        Func<Task> act = () => provider.GetCurrencyLiveRatesAsync("eur");

        await act.Should().NotThrowAsync();
        _loggerMock.Verify(l => l.ErrorAsync("ECB exchange rate provider", It.IsAny<Exception>(), It.IsAny<Customer>()),
            Times.Once);
    }

    #region Nested test doubles

    /// <summary>
    /// Returns a fixed HTTP response for every request, without touching the network.
    /// </summary>
    private class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public StaticResponseHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "text/xml")
            };

            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Simulates an HTTP-level failure (e.g. network error) by throwing from SendAsync,
    /// which GetStreamAsync surfaces as an exception the provider must catch.
    /// </summary>
    private class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Simulated network failure");
        }
    }

    #endregion
}
