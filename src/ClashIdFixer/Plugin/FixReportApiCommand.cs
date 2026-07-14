using Autodesk.Navisworks.Api.Plugins;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// Fallback copy of the api-mode command on the standard Add-ins tab, in
    /// case the custom "BIM УП" ribbon tab fails to load on some installation.
    /// Same logic - both buttons call <see cref="FixReportRunner"/>.
    /// </summary>
    [PluginAttribute("ClashIdFixer.FixReportApi", "EGRN",
        ToolTip = "Заменить Id в XML-отчёте по живым результатам Clash Detective открытого документа (без поиска по путям и координатам)",
        DisplayName = "Исправить ID в отчёте (Clash Detective)")]
    public class FixReportApiCommand : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            return FixReportRunner.RunApi();
        }
    }
}
