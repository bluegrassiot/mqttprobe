using FluentValidation;
using MqttProbe.Models.Mqtt;

namespace MqttProbe.Services.Mqtt;

public class ConnectionValidator : AbstractValidator<Connection>
{
    public ConnectionValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Host)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(host => Uri.CheckHostName(host) != UriHostNameType.Unknown)
            .WithMessage("Host must be a valid hostname or IP address.");
        RuleFor(x => x.Port)
            .InclusiveBetween(1, 65535)
            .WithMessage("Port must be between 1 and 65535.");
    }

    public Func<object, string, Task<IEnumerable<string>>> ValidateValue => async (model, propertyName) =>
    {
        var result =
            await ValidateAsync(ValidationContext<Connection>.CreateWithOptions((Connection)model,
                x => x.IncludeProperties(propertyName)));
        return result.IsValid ? Array.Empty<string>() : result.Errors.Select(e => e.ErrorMessage);
    };
}
