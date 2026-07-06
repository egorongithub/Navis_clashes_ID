using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using ClashIdFixer.Config;
using ClashIdFixer.Core;
using ClashIdFixer.UI;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// Optional helper button: the mandatory pre-report routine (show all ->
    /// hide the excluded set -> recompute all clash tests). After it finishes the
    /// user writes the report from Clash Detective as usual (Отчёт -> Записать
    /// отчёт) and then runs "Исправить ID в отчёте (XML)".
    /// </summary>
    [PluginAttribute("ClashIdFixer.Prepare", "EGRN",
        ToolTip = "Показать все элементы, скрыть непроверяемые и пересчитать проверки перед выгрузкой отчёта",
        DisplayName = "Подготовка и пересчёт проверок")]
    public class PrepareModelCommand : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            var log = new List<string>();
            try
            {
                Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (document == null)
                {
                    MessageBox.Show("Нет открытого документа Navisworks.", "ClashIdFixer",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var config = ClashIdFixerConfig.LoadOrCreateDefault(ClashIdFixerConfig.GetDefaultPath(pluginDir));

                using (var progress = new ProgressForm("ClashIdFixer — подготовка модели"))
                {
                    progress.Show();

                    if (config.ShowAllBeforeUpdate)
                    {
                        progress.SetMarquee("Показ всех элементов...");
                        ModelPreparation.ShowAll(document, log);
                    }

                    if (config.HideExcludedBeforeUpdate)
                    {
                        progress.SetMarquee("Скрытие непроверяемых элементов...");
                        ModelPreparation.HideNamedSet(document, config.HiddenSelectionSetName, log);
                    }

                    progress.SetMarquee("Пересчёт проверок Clash Detective... (может занять несколько минут)");
                    ModelPreparation.UpdateAllTests(document, log);

                    progress.Close();
                }

                log.Add("");
                log.Add("Готово. Теперь выгрузите отчёт из Clash Detective как обычно (Отчёт -> Записать отчёт, формат XML), " +
                        "после чего нажмите \"Исправить ID в отчёте (XML)\".");

                MessageBox.Show(string.Join(Environment.NewLine, log), "ClashIdFixer — подготовка завершена",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, log) + Environment.NewLine + Environment.NewLine + ex,
                    "ClashIdFixer — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
