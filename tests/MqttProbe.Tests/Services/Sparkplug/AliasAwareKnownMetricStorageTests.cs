using MqttProbe.Services.Sparkplug;
using SparkplugNet.Core.Enumerations;
using SparkplugNet.VersionB.Data;

namespace MqttProbe.Shared.Tests.Services.Sparkplug;

/// <summary>
/// Regression tests for the alias-aware KnownMetricStorage.
/// These exercise real SparkplugNet FilterMetrics behaviour — not mocked ISparkplugNode.
/// </summary>
[TestFixture]
public class AliasAwareKnownMetricStorageTests
{
    // ---------------------------------------------------------------
    // NDATA (NodeData) — alias-only metrics must survive filtering
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_AliasOnlyMetric_SurvivesFiltering()
    {
        // Arrange: birth metric with name + alias
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        // Act: data metric with alias only (Name = null, as NodeRunners produces)
        var dataMetric = new Metric(DataType.Double, 42.0);
        dataMetric.Name = null!;
        dataMetric.Alias = 1;

        var result = storage.FilterMetrics(new[] { dataMetric }, SparkplugMessageType.NodeData).ToList();

        // Assert
        result.Should().HaveCount(1);
        result[0].Alias.Should().Be(1);
        result[0].DataType.Should().Be(DataType.Double);
    }

    [Test]
    public void FilterMetrics_NData_MultipleAliasOnlyMetrics_AllSurvive()
    {
        var birthMetrics = new List<Metric>
        {
            new("Temperature", DataType.Double, 1.0) { Alias = 1 },
            new("Pressure", DataType.Double, 101.3) { Alias = 2 },
            new("Status", DataType.Boolean, false) { Alias = 3 },
        };
        var storage = new AliasAwareKnownMetricStorage(birthMetrics);

        var dataMetrics = new List<Metric>
        {
            new(DataType.Double, 25.0) { Name = null!, Alias = 1 },
            new(DataType.Double, 102.5) { Name = null!, Alias = 2 },
            new(DataType.Boolean, true) { Name = null!, Alias = 3 },
        };

        var result = storage.FilterMetrics(dataMetrics, SparkplugMessageType.NodeData).ToList();

        result.Should().HaveCount(3);
    }

    // ---------------------------------------------------------------
    // DDATA (DeviceData) — same behaviour for device metrics
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_DData_AliasOnlyMetric_SurvivesFiltering()
    {
        var birthMetric = new Metric("Humidity", DataType.Double, 55.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var dataMetric = new Metric(DataType.Double, 60.0);
        dataMetric.Name = null!;
        dataMetric.Alias = 1;

        var result = storage.FilterMetrics(new[] { dataMetric }, SparkplugMessageType.DeviceData).ToList();

        result.Should().HaveCount(1);
        result[0].Alias.Should().Be(1);
    }

    // ---------------------------------------------------------------
    // Birth messages — must still work correctly (name-based)
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NBirth_MetricWithNameAndAlias_Passes()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var result = storage.FilterMetrics(new[] { birthMetric }, SparkplugMessageType.NodeBirth).ToList();

        result.Should().HaveCount(1);
    }

    [Test]
    public void FilterMetrics_DBirth_MetricWithNameAndAlias_Passes()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var result = storage.FilterMetrics(new[] { birthMetric }, SparkplugMessageType.DeviceBirth).ToList();

        result.Should().HaveCount(1);
    }

    [Test]
    public void FilterMetrics_NBirth_AliasOnlyMetric_Rejected()
    {
        // Birth messages must have names — alias-only should be rejected by base
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var aliasOnlyBirth = new Metric(DataType.Double, 1.0);
        aliasOnlyBirth.Name = null!;
        aliasOnlyBirth.Alias = 1;

        var result = storage.FilterMetrics(new[] { aliasOnlyBirth }, SparkplugMessageType.NodeBirth).ToList();

        result.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // Unknown alias — must be rejected (no arbitrary aliases)
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_UnknownAlias_Rejected()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        // Alias 99 was never registered in birth
        var dataMetric = new Metric(DataType.Double, 42.0);
        dataMetric.Name = null!;
        dataMetric.Alias = 99;

        var result = storage.FilterMetrics(new[] { dataMetric }, SparkplugMessageType.NodeData).ToList();

        result.Should().BeEmpty();
    }

    [Test]
    public void FilterMetrics_DData_UnknownAlias_Rejected()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var dataMetric = new Metric(DataType.Double, 42.0);
        dataMetric.Name = null!;
        dataMetric.Alias = 99;

        var result = storage.FilterMetrics(new[] { dataMetric }, SparkplugMessageType.DeviceData).ToList();

        result.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // Datatype mismatch — must be rejected
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_DatatypeMismatch_Rejected()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        // Same alias but wrong datatype (Int64 instead of Double)
        var dataMetric = new Metric(DataType.Int64, 42L);
        dataMetric.Name = null!;
        dataMetric.Alias = 1;

        var result = storage.FilterMetrics(new[] { dataMetric }, SparkplugMessageType.NodeData).ToList();

        result.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // Name-based data metrics — still handled by base
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_NameOnlyMetric_Passes()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var dataMetric = new Metric("Temperature", DataType.Double, 42.0);

        var result = storage.FilterMetrics(new[] { dataMetric }, SparkplugMessageType.NodeData).ToList();

        result.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------
    // No aliases — storage behaves identically to base
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_NoAliases_NameBasedFiltering_Works()
    {
        var birthMetrics = new List<Metric>
        {
            new("Temperature", DataType.Double, 1.0),
            new("Pressure", DataType.Double, 101.3),
        };
        var storage = new AliasAwareKnownMetricStorage(birthMetrics);

        var dataMetrics = new List<Metric>
        {
            new("Temperature", DataType.Double, 42.0),
            new("Unknown", DataType.Double, 0.0), // not in birth — should be rejected
        };

        var result = storage.FilterMetrics(dataMetrics, SparkplugMessageType.NodeData).ToList();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Temperature");
    }

    // ---------------------------------------------------------------
    // Mixed: alias-only and name-based in same batch
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_MixedAliasAndName_BothHandled()
    {
        var birthMetrics = new List<Metric>
        {
            new("Temperature", DataType.Double, 1.0) { Alias = 1 },
            new("Pressure", DataType.Double, 101.3),
        };
        var storage = new AliasAwareKnownMetricStorage(birthMetrics);

        var dataMetrics = new List<Metric>
        {
            new(DataType.Double, 42.0) { Name = null!, Alias = 1 }, // alias-only
            new("Pressure", DataType.Double, 102.5),                // name-based
        };

        var result = storage.FilterMetrics(dataMetrics, SparkplugMessageType.NodeData).ToList();

        result.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------
    // No-name, no-alias metric — rejected (per base logic)
    // ---------------------------------------------------------------

    [Test]
    public void FilterMetrics_NData_NoNameNoAlias_Rejected()
    {
        var birthMetric = new Metric("Temperature", DataType.Double, 1.0);
        birthMetric.Alias = 1;
        var storage = new AliasAwareKnownMetricStorage(new[] { birthMetric });

        var badMetric = new Metric(DataType.Double, 42.0);
        badMetric.Name = null!;

        var result = storage.FilterMetrics(new[] { badMetric }, SparkplugMessageType.NodeData).ToList();

        result.Should().BeEmpty();
    }
}
