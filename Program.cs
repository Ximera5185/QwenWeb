using QwenWeb.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Обычная регистрация, без сложных настроек
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery(); // Обязательно для форм
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();