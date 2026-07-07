using Autodesk.Navisworks.Api.Plugins;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// Fallback copy of the command on the standard Add-ins tab, in case the
    /// custom "BIM УП" ribbon tab fails to load on some installation. Same
    /// logic - both buttons call <see cref="FixReportRunner"/>.
    /// </summary>
    [PluginAttribute("ClashIdFixer.FixReport", "EGRN",
        ToolTip = "Заменить в стандартном XML-отчёте Clash Detective Id подобъектов на Id объектов (правильный выбор)",
        DisplayName = "Исправить ID в отчёте (XML)")]
    public class FixReportCommand : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            return FixReportRunner.Run();
        }
    }
}
