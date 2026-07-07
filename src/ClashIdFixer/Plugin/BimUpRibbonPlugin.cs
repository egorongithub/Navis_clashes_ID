using Autodesk.Navisworks.Api.Plugins;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// Own ribbon tab "BIM УП" with the fix-report button. The ribbon layout
    /// (BimUpRibbon.xaml) and the button icons are embedded resources of this
    /// assembly. A fallback copy of the command also lives on the Add-ins tab
    /// (see <see cref="FixReportCommand"/>) in case ribbon loading is blocked
    /// on some install.
    /// </summary>
    [Plugin("ClashIdFixer.BimUp", "EGRN",
        DisplayName = "BIM УП",
        ToolTip = "Инструменты BIM УП")]
    [RibbonLayout("BimUpRibbon.xaml")]
    [RibbonTab("ID_BIMUP_TAB", DisplayName = "BIM УП")]
    [Command("ID_BIMUP_FIXREPORT",
        DisplayName = "Исправить ID\nв отчёте",
        Icon = "FixId16.png",
        LargeIcon = "FixId32.png",
        ToolTip = "Заменить в XML-отчёте Clash Detective Id подобъектов на Id элементов (вкладка \"Объект\", графа \"Id\")")]
    public class BimUpRibbonPlugin : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            if (commandId == "ID_BIMUP_FIXREPORT")
                return FixReportRunner.Run();
            return 0;
        }

        public override CommandState CanExecuteCommand(string commandId)
        {
            return new CommandState(true);
        }
    }
}
