using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Kabatra.MagicTheGathering.Companion;
using Kabatra.MagicTheGathering.Companion.Core;
using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Kabatra.MagicTheGathering.Companion.Core.Cloud;
using Kabatra.MagicTheGathering.Companion.Core.Decks;
using Kabatra.MagicTheGathering.Companion.Core.History;
using Kabatra.MagicTheGathering.Companion.Core.Profiles;
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

// Official prebuilt deck lists from MTGJSON.
builder.Services.AddScoped(sp => new PreconCatalog(
    new HttpClient { BaseAddress = PreconCatalog.DefaultBaseAddress }, sp.GetRequiredService<IKeyValueStore>()));

// Supabase holds everyone's profiles, playgroups, decks and play history; signing in is required.
// Supabase:AnonKey is still read so a project on the legacy keys keeps working until it migrates.
var supabase = new SupabaseOptions(
    builder.Configuration["Supabase:Url"],
    builder.Configuration["Supabase:PublishableKey"] is { Length: > 0 } publishable ? publishable : builder.Configuration["Supabase:AnonKey"]);
builder.Services.AddSingleton(supabase);
builder.Services.AddScoped(sp => new SupabaseClient(new HttpClient(), supabase, sp.GetRequiredService<IKeyValueStore>()));
builder.Services.AddScoped<PlaygroupService>();
builder.Services.AddScoped<ProfileAccount>();

// Sign-in providers, e.g. "Auth": { "Providers": ["google", "apple"] }. Each must be turned on in Supabase Auth.
var providers = AuthProviders.From(builder.Configuration.GetSection("Auth:Providers").GetChildren().Select(c => c.Value));
builder.Services.AddSingleton(providers);
builder.Services.AddScoped<DeckLibrary>();
builder.Services.AddScoped(sp => new GameRecorder(sp.GetRequiredService<IKeyValueStore>()));
builder.Services.AddScoped(sp => new PlayHistory(sp.GetRequiredService<DeckLibrary>(), sp.GetRequiredService<GameRecorder>()));

await builder.Build().RunAsync();
