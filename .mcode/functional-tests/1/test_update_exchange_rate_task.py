"""
Functional tests for UpdateExchangeRateTask (nopCommerce modernization milestone).

These tests invoke the .NET test suite via dotnet CLI and verify all 11 NUnit tests
(8 unit + 3 integration) pass. The tests are tagged
[Category("UpdateExchangeRateTask")] in the C# test file at:
  src/Tests/Nop.Tests/Nop.Services.Tests/Directory/UpdateExchangeRateTaskTests.cs
"""

import os
import subprocess
import time
import pytest

WORKSPACE_DIR = os.path.join(os.environ['WORKSPACE_DIR'], 'nopCommerce')
TEST_PROJECT = r"src\Tests\Nop.Tests\Nop.Tests.csproj"
TEST_FILTER = "Category=UpdateExchangeRateTask"
CONFIGURATION = "Release"


def run_dotnet_test(extra_args=None):
    """Run dotnet test for the UpdateExchangeRateTask category and return the result."""
    cmd = [
        "dotnet", "test", TEST_PROJECT,
        "--filter", TEST_FILTER,
        "--no-build",
        "-c", CONFIGURATION,
        "--logger", "console;verbosity=detailed"
    ]
    if extra_args:
        cmd.extend(extra_args)

    start = time.time()
    result = subprocess.run(
        cmd,
        cwd=WORKSPACE_DIR,
        capture_output=True,
        text=True,
        timeout=300,
    )
    result.duration_ms = (time.time() - start) * 1000
    return result


class TestUpdateExchangeRateTaskUnitTests:
    """Unit tests (Moq-based) for UpdateExchangeRateTask."""

    @pytest.fixture(autouse=True, scope="class")
    def run_result(self, request):
        """Run the dotnet test suite once per class and store result."""
        result = run_dotnet_test()
        request.cls._result = result
        request.cls._output = result.stdout + result.stderr

    def test_auto_update_disabled_returns_immediately(self):
        """When AutoUpdateEnabled=false, task returns without calling ICurrencyService."""
        assert "Passed ExecuteAsync_WhenAutoUpdateDisabled_ShouldReturnImmediately_LegacyParityVerified" in self._output

    def test_auto_update_enabled_fetches_and_updates_currencies(self):
        """When AutoUpdateEnabled=true, task fetches live rates and updates each matching currency."""
        assert "Passed ExecuteAsync_WhenAutoUpdateEnabled_ShouldFetchAndUpdateAllMatchingCurrencies" in self._output

    def test_unknown_currency_code_skipped(self):
        """Currency code not found in DB is skipped; remaining currencies are still processed."""
        assert "Passed ExecuteAsync_WhenCurrencyCodeNotFoundInDatabase_ShouldSkipAndContinueProcessing_LegacyParityVerified" in self._output

    def test_exception_from_provider_propagates(self):
        """Exception from GetCurrencyLiveRatesAsync propagates (task marked failed by scheduler)."""
        assert "Passed ExecuteAsync_WhenGetCurrencyLiveRatesThrows_ShouldPropagateException_LegacyParityVerified" in self._output

    def test_empty_rates_list_no_updates(self):
        """Empty exchange rate list from provider results in no DB updates."""
        assert "Passed ExecuteAsync_WhenRatesListIsEmpty_ShouldCompleteWithoutAnyUpdates" in self._output

    def test_updated_on_utc_set_during_update(self):
        """UpdatedOnUtc is set to current UTC time when a currency is updated."""
        assert "Passed ExecuteAsync_WhenCurrencyIsUpdated_ShouldSetUpdatedOnUtcToCurrentUtcTime" in self._output

    def test_multiple_currencies_all_updated(self):
        """When provider returns multiple rates, all matching currencies are updated."""
        assert "Passed ExecuteAsync_WhenMultipleCurrenciesReturned_ShouldUpdateEachMatchingCurrency" in self._output

    def test_zero_rate_persisted(self):
        """Zero rate from provider is persisted (intentional modernization: validation is provider's responsibility)."""
        assert "Passed ExecuteAsync_WhenProviderReturnsZeroRate_ShouldPersistIt_IntentionalDifferenceFromLegacy" in self._output


class TestUpdateExchangeRateTaskIntegrationTests:
    """Integration tests (real DI + SQLite) for UpdateExchangeRateTask."""

    @pytest.fixture(autouse=True, scope="class")
    def run_result(self, request):
        """Run the dotnet test suite once per class and store result."""
        result = run_dotnet_test()
        request.cls._result = result
        request.cls._output = result.stdout + result.stderr

    def test_integration_auto_update_disabled(self):
        """Integration: AutoUpdateEnabled=false causes immediate return without DB access."""
        assert "Passed ExecuteAsync_Integration_WhenAutoUpdateDisabled_ShouldReturnImmediately_LegacyParityVerified" in self._output

    def test_integration_real_di_empty_provider(self):
        """Integration: real DI container + empty provider completes cleanly with no currency mutations."""
        assert "Passed ExecuteAsync_Integration_WithRealDependencies_AndEmptyProvider_ShouldCompleteCleanly" in self._output

    def test_integration_test_provider_updates_db(self):
        """Integration: TestExchangeRateProviderWithRates drives end-to-end rate update in SQLite."""
        assert "Passed ExecuteAsync_Integration_WithTestExchangeRateProviderWithRates_ShouldUpdateCurrencyInDatabase" in self._output


class TestSuiteOverall:
    """Overall suite health check."""

    @pytest.fixture(autouse=True, scope="class")
    def run_result(self, request):
        result = run_dotnet_test()
        request.cls._result = result
        request.cls._output = result.stdout + result.stderr

    def test_suite_passed(self):
        """All UpdateExchangeRateTask tests must pass (individual tests verified by name above)."""
        assert self._result.returncode == 0, (
            f"dotnet test exited with code {self._result.returncode}.\n"
            f"stdout:\n{self._result.stdout}\n"
            f"stderr:\n{self._result.stderr}"
        )
        assert "Test Run Successful" in self._output
