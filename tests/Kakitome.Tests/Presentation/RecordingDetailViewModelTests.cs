using System.Reflection;
using Kakitome.Application.Asr;
using Kakitome.Application.Library;
using Kakitome.Domain.Library;
using Kakitome.Presentation.Services;
using Kakitome.Presentation.ViewModels;
using Kakitome.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Kakitome.Tests.Presentation;

public sealed class RecordingDetailViewModelTests
{
    [Fact]
    public async Task Opening_a_recording_deleted_outside_Kakitome_shows_a_message_instead_of_throwing()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var entry = await f.Library.CreateRecordingAsync(new NewRecording { Project = "A", Title = "gone" });
        Directory.Delete(f.PathOf(entry.Folder), recursive: true); // the index still lists it

        var vm = new RecordingDetailViewModel(
            f.Library,
            f.Services.GetRequiredService<RecordingActions>(),
            new ImmediateDispatcher(),
            new KeyLocalizer(),
            DispatchProxy.Create<IShellService, UnusedProxy>(),
            f.Services.GetRequiredService<GlossaryTips>(),
            f.Services.GetRequiredService<GlossaryService>());

        await vm.LoadAsync(new RecordingNavigation(entry.Id));

        Assert.Equal("Error_RecordingMissing", vm.Message);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class KeyLocalizer : ILocalizer
    {
        public string GetString(string key) => key;

        public string Format(string key, params object?[] args) => key;
    }

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }
}
