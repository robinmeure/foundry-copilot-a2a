using FoundryCopilotA2A.Adapter;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var (adapterOptions, authenticationOptions) = builder.AddAdapterServices();

var app = builder.Build();
app.UseCors("spa");

if (authenticationOptions.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Metadata and trace ownership depend on the authenticated caller.
app.UseMiddleware<A2ARequestContextMiddleware>();
app.UseMiddleware<A2AStreamingFinalMiddleware>();
app.UseMiddleware<TraceContextMiddleware>();

app.MapAdapterEndpoints(adapterOptions, authenticationOptions);
app.MapAgentCards(adapterOptions, authenticationOptions);
app.Run();

public partial class Program;
