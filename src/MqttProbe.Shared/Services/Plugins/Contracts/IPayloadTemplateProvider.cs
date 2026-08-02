namespace MqttProbe.Services.Plugins.Contracts;

public interface IPayloadTemplateProvider
{
    public string FormatId { get; }
    public IReadOnlyList<PayloadTemplateOption> GetOptions();
}

public sealed class PayloadTemplateOption
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyDictionary<string, object>? DefaultValues { get; init; }
}
