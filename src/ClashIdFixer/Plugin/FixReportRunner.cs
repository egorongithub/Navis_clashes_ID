using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using ClashIdFixer.Config;
using ClashIdFixer.Core;
using ClashIdFixer.UI;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// The actual command logic, shared by the "BIM УП" ribbon buttons and the
    /// fallback buttons on the Add-ins tab. Two modes over the same summary /
    /// errors-file plumbing:
    ///  - path mode: elements are found in the model by the report's tree path
    ///    (+ id + clash point) - works for any report, even for a foreign model;
    ///  - api mode: report entries are matched to the LIVE Clash Detective
    ///    results of the open document, elements come straight from the result -
    ///    no path/geometry matching, but the open file must contain the tests.
    /// </summary>
    internal static class FixReportRunner
    {
        public static int Run()
        {
            return RunCore(false);
        }

        public static int RunApi()
        {
            return RunCore(true);
        }

        private static int RunCore(bool useClashApi)
        {
            try
            {
                Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (document == null)
                {
                    MessageBox.Show(
                        "Нет открытого документа. Откройте ту же модель, по которой был сделан отчёт, и повторите.",
                        "ClashIdFixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var config = ClashIdFixerConfig.LoadOrCreateDefault(ClashIdFixerConfig.ResolveConfigPath(pluginDir));

                string inputFile;
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "Выберите XML-отчёт Clash Detective";
                    dialog.Filter = "XML-отчёт Clash Detective (*.xml)|*.xml|Все файлы (*.*)|*.*";
                    dialog.InitialDirectory = KnownFolders.Downloads;
                    dialog.CheckFileExists = true;

                    if (dialog.ShowDialog() != DialogResult.OK) return 0;
                    inputFile = dialog.FileName;
                }

                string outputFolder = string.IsNullOrWhiteSpace(config.OutputFolder)
                    ? KnownFolders.Downloads
                    : config.OutputFolder;
                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

                // Separate suffixes so both modes can be run on the same report
                // and compared side by side.
                string baseName = Path.GetFileNameWithoutExtension(inputFile);
                string suffix = useClashApi ? "_fixed_api" : "_fixed";
                string outputFile = Path.Combine(outputFolder, baseName + suffix + ".xml");

                FixReportResult result;
                var diagnostics = new StringBuilder();
                string title = useClashApi
                    ? "ClashIdFixer — исправление ID (через Clash Detective)"
                    : "ClashIdFixer — исправление ID в отчёте";
                using (var progress = new ProgressForm(title))
                {
                    progress.Show();
                    progress.SetMarquee("Чтение отчёта: " + Path.GetFileName(inputFile));

                    Action<int, int> onProgress = (current, total) =>
                    {
                        if (current % 20 == 0 || current == total)
                            progress.SetProgress(
                                string.Format("Обработка записей отчёта: {0} из {1}", current, total),
                                current, total);
                    };

                    result = useClashApi
                        ? ClashApiPostProcessor.Fix(document, inputFile, outputFile, config, onProgress, diagnostics)
                        : ReportPostProcessor.Fix(document, inputFile, outputFile, config, onProgress, diagnostics);

                    progress.Close();
                }

                // Every problem entry goes to a separate errors file instead of the
                // summary window.
                string errorsFile = null;
                if (result.Errors.Count > 0)
                {
                    errorsFile = Path.Combine(outputFolder, baseName + (useClashApi ? "_errors_api" : "_errors") + ".txt");
                    var errorsText = new StringBuilder();
                    errorsText.AppendLine("Отчёт: " + inputFile);
                    errorsText.AppendLine(result.UnitsSummary ?? "");
                    errorsText.AppendLine(string.Format("Пропущено записей (Id оставлен без изменений): {0}", result.Errors.Count));
                    errorsText.AppendLine(new string('-', 60));
                    foreach (var error in result.Errors)
                        errorsText.AppendLine(error);

                    if (diagnostics.Length > 0)
                    {
                        errorsText.AppendLine(new string('-', 60));
                        errorsText.AppendLine("ПОДРОБНАЯ ДИАГНОСТИКА (первые случаи каждого вида):");
                        errorsText.AppendLine(diagnostics.ToString());
                    }
                    File.WriteAllText(errorsFile, errorsText.ToString(), Encoding.UTF8);
                }

                int errorCount = result.Errors.Count;
                var message = new StringBuilder();
                message.AppendLine(string.Format("Элементов коллизий в отчёте: {0}", result.ClashObjectCount));
                message.AppendLine(string.Format("Id исправлен: {0}", result.IdReplaced));
                message.AppendLine(string.Format("Id уже правильный: {0}", result.AlreadyCorrect));
                message.AppendLine(string.Format("Пропущено (см. файл ошибок): {0}", errorCount));
                message.AppendLine();
                message.AppendLine("Файл: " + result.OutputFile);
                if (errorsFile != null)
                {
                    message.AppendLine();
                    message.AppendLine("Файл ошибок: " + errorsFile);
                }

                MessageBox.Show(message.ToString(), "ClashIdFixer — готово",
                    MessageBoxButtons.OK,
                    errorCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                var invalid = ex as InvalidOperationException;
                if (invalid != null)
                {
                    // Expected situations (no clash tests in the document, broken
                    // config...) are reported as plain text, without a stack trace.
                    MessageBox.Show(invalid.Message, "ClashIdFixer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
                MessageBox.Show(ex.ToString(), "ClashIdFixer — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
