using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Kabatra.MagicTheGathering.Companion;
using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IKeyValueStore, BrowserStorage>();
builder.Services.AddScoped<GameSession>();

// Card data from Scryfall, cached on the device.
builder.Services.AddScoped(_ => new ScryfallClient(new HttpClient { BaseAddress = ScryfallClient.DefaultBaseAddress }));
builder.Services.AddScoped<CardCatalog>();

// Shared decks in Supabase. Leave Supabase:Url / Supabase:PublishableKey empty in appsettings.json to keep decks on the device.
// Supabase:AnonKey is still read so a project on the legacy keys keeps working until it migrates.
var supabase = new SupabaseOptions(
    builder.Configuration["Supabase:Url"],
    builder.Configuration["Supabase:PublishableKey"] is { Length: > 0 } publishable ? publishable : builder.Configuration["Supabase:AnonKey"]);
builder.Services.AddSingleton(supabase);
builder.Services.AddScoped(sp => new SupabaseClient(new HttpClient(), supabase, sp.GetRequiredService<IKeyValueStore>()));
builder.Services.AddScoped<PlaygroupService>();
builder.Services.AddScoped<DeckLibrary>();

await builder.Build().RunAsync();
