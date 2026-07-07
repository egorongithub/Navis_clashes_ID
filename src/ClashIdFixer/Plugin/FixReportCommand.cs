using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using ClashIdFixer.Config;
using ClashIdFixer.Core;
using ClashIdFixer.UI;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// The core of the plugin. Takes the STANDARD Clash Detective XML report -
    /// written by the user from the Clash Detective window with all their own
    /// report settings - and, as a final pass, substitutes the sub-object values
    /// with the values of the composite object ("correct selection" level).
    /// Nothing about the report itself is re-invented; only ids/values change.
    /// </summary>
    [PluginAttribute("ClashIdFixer.FixReport", "EGRN",
        ToolTip = "Заменить в стандартном XML-отчёте Clash Detective Id подобъектов на Id объектов (правильный выбор)",
        DisplayName = "Исправить ID в отчёте (XML)")]
    public class FixReportCommand : AddInPlugin
    {
        public override int Execute(params string[] parameters)
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

                string outputFile = Path.Combine(outputFolder,
                    Path.GetFileNameWithoutExtension(inputFile) + "_fixed.xml");

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

                string diagFile = null;
                if (diagnostics.Length > 0)
                {
                    diagFile = Path.Combine(outputFolder,
                        Path.GetFileNameWithoutExtension(inputFile) + "_diag.txt");
                    File.WriteAllText(diagFile, diagnostics.ToString(), Encoding.UTF8);
                }

                var message = new StringBuilder();
                message.AppendLine(string.Format("Элементов коллизий в отчёте: {0}", result.ClashObjectCount));
                message.AppendLine(string.Format("Найдено в открытой модели: {0}", result.PathResolved));
                message.AppendLine(string.Format("Id заменён на \"Объект/Id\": {0}", result.IdReplaced));
                message.AppendLine(string.Format("Id уже правильный (замена не нужна): {0}", result.AlreadyCorrect));
                message.AppendLine(string.Format("Свойство \"Объект/Id\" не найдено вверх по дереву: {0}", result.TrueIdNotFound));
                message.AppendLine(string.Format("В отчёте нет атрибута с ID (см. настройки): {0}", result.NoIdAttribute));
                message.AppendLine(string.Format("Не найдено в модели по пути из отчёта: {0}", result.PathUnresolved));
                message.AppendLine(string.Format("Всего заменено значений: {0}", result.ValuesReplaced));
                message.AppendLine();
                message.AppendLine("Файл: " + result.OutputFile);

                if (diagFile != null)
                {
                    message.AppendLine();
                    message.AppendLine("Создан файл диагностики с реальными именами категорий/свойств и путей:");
                    message.AppendLine(diagFile);
                }

                if (result.PathUnresolved > 0 && result.PathResolved == 0)
                {
                    message.AppendLine();
                    message.AppendLine("ПОХОЖЕ, ОТКРЫТА НЕ ТА МОДЕЛЬ: ни один элемент отчёта не найден в текущем документе. " +
                                       "Откройте файл, по которому делался отчёт, и повторите.");
                }

                MessageBox.Show(message.ToString(), "ClashIdFixer — готово",
                    MessageBoxButtons.OK,
                    result.PathUnresolved > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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
