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
    /// The actual command logic, shared by the "BIM УП" ribbon button and the
    /// fallback button on the Add-ins tab.
    /// </summary>
    internal static class FixReportRunner
    {
        public static int Run()
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
                var config = ClashIdFixerConfig.LoadOrCreateDefault(ClashIdFixerConfig.GetDefaultPath(pluginDir));

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

                string baseName = Path.GetFileNameWithoutExtension(inputFile);
                string outputFile = Path.Combine(outputFolder, baseName + "_fixed.xml");

                FixReportResult result;
                var diagnostics = new StringBuilder();
                using (var progress = new ProgressForm("ClashIdFixer — исправление ID в отчёте"))
                {
                    progress.Show();
                    progress.SetMarquee("Чтение отчёта: " + Path.GetFileName(inputFile));

                    result = ReportPostProcessor.Fix(document, inputFile, outputFile, config,
                        (current, total) =>
                        {
                            if (current % 20 == 0 || current == total)
                                progress.SetProgress(
                                    string.Format("Обработка элементов коллизий: {0} из {1}", current, total),
                                    current, total);
                        },
                        diagnostics);

                    progress.Close();
                }

                // Every problem entry goes to a separate errors file instead of the
                // summary window.
                string errorsFile = null;
                if (result.Errors.Count > 0)
                {
                    errorsFile = Path.Combine(outputFolder, baseName + "_errors.txt");
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
                MessageBox.Show(ex.ToString(), "ClashIdFixer — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
