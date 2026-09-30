using Brio.Services;
using Brio.Resources;
using Brio.UI;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using System;

namespace Brio.Game.Chat;

public class CommandHandlerService : IDisposable
{
    private const string BrioCommandName = "/brio";
    private const string XATCommandName = "/xat";
    private const string MCDFCommandName = "/mcdf";

    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chatGui;
    private readonly UIManager _uiManager;
    private readonly Mediator _mediator;

    public CommandHandlerService(ICommandManager commandManager, IChatGui chatGui, UIManager uiManager, Mediator mediator)
    {
        _commandManager = commandManager;
        _chatGui = chatGui;
        _uiManager = uiManager;
        _mediator = mediator;

        _commandManager.AddHandler(BrioCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = global::Brio.Resources.Localize.Text("Toggles the Brio window."),
            ShowInHelp = true,
        });
        _commandManager.AddHandler(XATCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = global::Brio.Resources.Localize.Text("Toggles the Brio window."),
            ShowInHelp = false,
        });
        _commandManager.AddHandler(MCDFCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Localize.Text("Toggles Brio's MCDF window."),
            ShowInHelp = false,
        });
    }

    private void OnCommand(string command, string arguments)
    {
        if(command == MCDFCommandName)
        {
            _uiManager.ToggleMCDFWindow();
            return;
        }

        if(arguments.Length == 0)
            arguments = "window";

        var argumentList = arguments.Split(' ', 2);

        switch(argumentList[0].ToLowerInvariant())
        {
            case "window":
                _uiManager.ToggleMainWindow();
                break;

            case "timeline":
                _uiManager.ToggleTimelineWindow();
                break;

            case "settings":
                _uiManager.ToggleSettingsWindow();
                break;

            case "about":
                _uiManager.ToggleWelcomeWindow();
                break;

            case "mcdf":
                _uiManager.ToggleMCDFWindow();
                break;

            case "mediator":
                _mediator.PrintSubscriberInfo();
                break;

            case "help":
            default:
                PrintHelp();
                break;
        }

    }

    private void PrintHelp()
    {
        _chatGui.Print(global::Brio.Resources.Localize.Text("Valid Brio Commands Are:"));
        _chatGui.Print(global::Brio.Resources.Localize.Text("<none> - Toggle main Brio window"));
        _chatGui.Print(global::Brio.Resources.Localize.Text("window - Toggle main Brio window"));
        _chatGui.Print(global::Brio.Resources.Localize.Text("settings - Toggle Brio settings window"));
        _chatGui.Print(global::Brio.Resources.Localize.Text("about - Toggle Brio info window"));
        _chatGui.Print(global::Brio.Resources.Localize.Text("help - Print this help prompt"));
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(BrioCommandName);
        _commandManager.RemoveHandler(XATCommandName);
        _commandManager.RemoveHandler(MCDFCommandName);
    }
}
