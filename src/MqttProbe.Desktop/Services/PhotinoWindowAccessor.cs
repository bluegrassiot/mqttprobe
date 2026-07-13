using Photino.NET;

namespace MqttProbe.Desktop.Services;

public interface IPhotinoWindowAccessor
{
    public PhotinoWindow? Window { get; set; }
}

public class PhotinoWindowAccessor : IPhotinoWindowAccessor
{
    public PhotinoWindow? Window { get; set; }
}
