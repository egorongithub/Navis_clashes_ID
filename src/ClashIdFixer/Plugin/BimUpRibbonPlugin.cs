using Autodesk.Navisworks.Api.Plugins;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// Own ribbon tab "BIM УП" with the fix-report button.
    ///
    /// Navisworks resolves the ribbon layout, the display-name strings and the
    /// icons as LOOSE FILES next to the plugin DLL (NOT embedded resources):
    ///   &lt;plugin dir&gt;\en-US\ClashIdFixer.xaml   (RibbonLayout)
    ///   &lt;plugin dir&gt;\en-US\ClashIdFixer.name   (Strings)
    ///   &lt;plugin dir&gt;\Resources\FixId16/32.png  (Command icons)
    /// A ru-RU copy of the xaml/name is shipped too, so a Russian-language
    /// Navisworks finds them in its own locale folder as well as via the en-US
    /// fallback. LoadForCanExecute forces the plugin to load at start-up so the
    /// tab is present immediately. A fallback copy of the command also lives on
    /// the Add-ins tab (see <see cref="FixReportCommand"/>).
    /// </summary>
    [Plugin("ClashIdFixer.BimUp", "EGRN",
        DisplayName = "BIM УП",
        ToolTip = "Инструменты BIM УП")]
    [Strings("ClashIdFixer.name")]
    [RibbonLayout("ClashIdFixer.xaml")]
    [RibbonTab("ID_BIMUP_TAB",
        DisplayName = "BIM УП",
        LoadForCanExecute = true)]
    [Command("ID_BIMUP_FIXREPORT",
        LoadForCanExecute = true,
        DisplayName = "Исправить ID\nв отчёте",
        Icon = "Resources\\FixId16.png",
        LargeIcon = "Resources\\FixId32.png",
        ToolTip = "Заменить в XML-отчёте Clash Detective Id подобъектов на Id элементов (вкладка \"Объект\", графа \"Id\")")]
    [Command("ID_BIMUP_FIXREPORT_API",
        LoadForCanExecute = true,
        DisplayName = "Исправить ID\n(Clash Detective)",
        Icon = "Resources\\FixId16.png",
        LargeIcon = "Resources\\FixId32.png",
        ToolTip = "Заменить Id в XML-отчёте по живым результатам Clash Detective открытого документа (без поиска по путям и координатам)")]
    public class BimUpRibbonPlugin : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            if (commandId == "ID_BIMUP_FIXREPORT")
                return FixReportRunner.Run();
            if (commandId == "ID_BIMUP_FIXREPORT_API")
                return FixReportRunner.RunApi();
            return 0;
        }

        public override CommandState CanExecuteCommand(string commandId)
        {
            return new CommandState(true);
        }
    }
}
