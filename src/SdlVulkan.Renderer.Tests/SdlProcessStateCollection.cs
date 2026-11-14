using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The one collection for every test that sets or reads SDL's PROCESS-WIDE state: hints, app metadata, and
/// <c>SDL_Init</c>/<c>SDL_Quit</c>. They are one piece of state, not three: <c>SDL_Quit</c> tears down SDL's global
/// properties and hints, so a test that inits and quits video (<see cref="ScreenSaverHintTests"/>) erases the
/// metadata another test just applied. With hints and metadata in two collections, xunit ran them in parallel and
/// <see cref="AppMetadataTests.ApplyingTheFallbackNamesTheEntryAssembly"/> read the version back as null about once
/// in a few full runs (measured: apply, then <c>SDL_Init(Video)</c> + <c>SDL_Quit</c> on the dummy driver, and the
/// version reads back null). A new test of any of the three joins this collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SdlProcessStateCollection
{
    public const string Name = "SdlProcessState";
}
