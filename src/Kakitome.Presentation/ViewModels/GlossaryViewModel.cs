using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kakitome.Application.Asr;
using Kakitome.Application.Library;
using Kakitome.Application.Settings;
using Kakitome.Presentation.Services;

namespace Kakitome.Presentation.ViewModels;

/// <summary>
/// Settings › Transcription › Glossary (ADR-030): choose where a glossary applies (all recordings or one project),
/// import a text file, edit it, or stop using it. Nothing is built in; the user (or an AI assistant they use)
/// writes the list, guided by the format and the copyable request text shown here.
/// </summary>
public sealed partial class GlossaryViewModel(
    GlossaryService glossaries, RecordingActions actions, IShellService shell, ILocalizer text, ISettingsStore settings)
    : ObservableObject
{
    public ObservableCollection<Choice> Scopes { get; } = [];

    [ObservableProperty]
    public partial Choice? Scope { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool Exists { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    public string FormatHelp => text.GetString("Glossary_Format");

    public string AiRequest => text.GetString("Glossary_AiRequest");

    /// <summary>The "make a glossary" tip on recordings without one (ADR-031).</summary>
    public bool ShowTips
    {
        get => settings.Current.Processing.GlossaryTips;
        set
        {
            if (value != ShowTips)
            {
                _ = settings.UpdateAsync(s => s.Processing.GlossaryTips = value);
                OnPropertyChanged();
            }
        }
    }

    public async Task LoadAsync()
    {
        var selected = Scope?.Value;
        var projects = await actions.ListProjectsAsync().ConfigureAwait(true);
        Scopes.Clear();
        Scopes.Add(new Choice(null, text.GetString("Glossary_ScopeAll")));
        foreach (var p in projects)
        {
            Scopes.Add(new Choice(p.Name, text.Format("Glossary_ScopeProject", p.Name)));
        }

        Scope = Scopes.FirstOrDefault(s => s.Value == selected) ?? Scopes[0];
        await RefreshAsync().ConfigureAwait(true);
    }

    partial void OnScopeChanged(Choice? value)
    {
        Message = null;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (await shell.PickTextFileAsync().ConfigureAwait(true) is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            var result = await glossaries.ImportAsync(CurrentScope, path).ConfigureAwait(true);
            var message = text.Format("Glossary_Imported", result.Glossary.Terms.Count, result.Glossary.Replacements.Count);
            if (result.Glossary.Issues.Count > 0)
            {
                var first = result.Glossary.Issues[0];
                message += " " + text.Format("Glossary_Skipped", result.Glossary.Issues.Count, first.Line.ToString(CultureInfo.CurrentCulture), first.Text);
            }

            if (result.PreservedPreviousFile is { } kept)
            {
                message += " " + text.Format("Glossary_PreviousKept", kept);
            }

            ShowMessage(message + " " + text.GetString("Glossary_ApplyHint"), error: false);
        }
        catch (InvalidDataException ex)
        {
            ShowMessage(text.GetString("Glossary_Invalid_" + ex.Message), error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(text.Format("Glossary_Failed", ex.Message), error: true);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Opens the glossary in the default text editor, creating it (format notes only) first if needed.</summary>
    [RelayCommand]
    private async Task EditAsync()
    {
        var japanese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
        var path = await glossaries.EnsureFileAsync(CurrentScope, japanese).ConfigureAwait(true);
        shell.OpenTextFile(path);
        ShowMessage(text.GetString("Glossary_EditHint"), error: false);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(Exists))]
    private async Task RemoveAsync()
    {
        if (!await shell.ConfirmAsync(text.GetString("Glossary_RemoveTitle"), text.GetString("Glossary_RemoveContent"),
                text.GetString("Glossary_RemovePrimary"), text.GetString("Dialog_Cancel")).ConfigureAwait(true))
        {
            return;
        }

        if (glossaries.Remove(CurrentScope) is { } kept)
        {
            ShowMessage(text.Format("Glossary_Removed", kept), error: false);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CopyAiRequest()
    {
        shell.CopyText(AiRequest);
        ShowMessage(text.GetString("Glossary_Copied"), error: false);
    }

    private GlossaryScope CurrentScope => new(Scope?.Value);

    private async Task RefreshAsync()
    {
        var info = await glossaries.GetInfoAsync(CurrentScope).ConfigureAwait(true);
        Exists = info.Exists;
        Status = info.Exists
            ? text.Format("Glossary_Status", info.Terms, info.Replacements)
            : text.GetString("Glossary_None");
    }

    private void ShowMessage(string message, bool error)
    {
        MessageIsError = error;
        Message = message;
    }
}
