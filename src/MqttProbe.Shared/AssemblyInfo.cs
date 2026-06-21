using System.Runtime.CompilerServices;

// Expose internal members to the unit test project so component
// methods can be called directly in bUnit tests without reflection.
[assembly: InternalsVisibleTo("MqttProbe.Shared.Tests")]
