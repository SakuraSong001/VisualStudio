﻿using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GitHub.Exports;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.Services;
using GitHub.UI;
using GitHub.ViewModels;
using GitHub.VisualStudio.Helpers;
using GitHub.VisualStudio.UI.Helpers;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using ReactiveUI;
using Task = System.Threading.Tasks.Task;
using GitHub.InlineReviews.Commands;

namespace GitHub.VisualStudio.UI.Views
{
    public class GenericPullRequestDetailView : ViewBase<IPullRequestDetailViewModel, GenericPullRequestDetailView>
    { }

    [ExportView(ViewType = UIViewType.PRDetail)]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public partial class PullRequestDetailView : GenericPullRequestDetailView
    {
        public PullRequestDetailView()
        {
            InitializeComponent();

            bodyMarkdown.PreviewMouseWheel += ScrollViewerUtilities.FixMouseWheelScroll;
            changesSection.PreviewMouseWheel += ScrollViewerUtilities.FixMouseWheelScroll;

            this.WhenActivated(d =>
            {
                d(ViewModel.OpenOnGitHub.Subscribe(_ => DoOpenOnGitHub()));
                d(ViewModel.OpenFile.Subscribe(x => DoOpenFile((IPullRequestFileNode)x)));
                d(ViewModel.DiffFile.Subscribe(x => DoDiffFile((IPullRequestFileNode)x).Forget()));
            });
        }

        [Import]
        ITeamExplorerServiceHolder TeamExplorerServiceHolder { get; set; }

        [Import]
        IVisualStudioBrowser VisualStudioBrowser { get; set; }

        [Import]
        IEditorOptionsFactoryService EditorOptionsFactoryService { get; set; }

        protected override void OnVisualParentChanged(DependencyObject oldParent)
        {
            base.OnVisualParentChanged(oldParent);
        }

        void DoOpenOnGitHub()
        {
            var repo = TeamExplorerServiceHolder.ActiveRepo;
            var browser = VisualStudioBrowser;
            var url = repo.CloneUrl.ToRepositoryUrl().Append("pull/" + ViewModel.Model.Number);
            browser.OpenUrl(url);
        }

        void DoOpenFile(IPullRequestFileNode file)
        {
            try
            {
                var fileName = ViewModel.GetLocalFilePath(file);
                Services.Dte.ItemOperations.OpenFile(fileName);
            }
            catch (Exception e)
            {
                ShowErrorInStatusBar("Error opening file", e);
            }
        }

        async Task DoDiffFile(IPullRequestFileNode file)
        {
            try
            {
                var fileNames = await ViewModel.ExtractDiffFiles(file);
                var relativePath = System.IO.Path.Combine(file.DirectoryPath, file.FileName);
                var fullPath = System.IO.Path.Combine(ViewModel.Repository.LocalPath, relativePath);
                var leftLabel = $"{relativePath};{ViewModel.TargetBranchDisplayName}";
                var rightLabel = $"{relativePath};PR {ViewModel.Model.Number}";
                var caption = $"Diff - {file.FileName}";
                var tooltip = $"{leftLabel}\nvs.\n{rightLabel}";
                var options = __VSDIFFSERVICEOPTIONS.VSDIFFOPT_DetectBinaryFiles |
                    __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary;

                if (!ViewModel.IsCheckedOut)
                {
                    options |= __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary;
                }

                IVsWindowFrame frame;
                using (new NewDocumentStateScope(__VSNEWDOCUMENTSTATE.NDS_Provisional, VSConstants.NewDocumentStateReason.SolutionExplorer))
                {
                    // Diff window will open in provisional (right hand) tab until document is touched.
                    frame = Services.DifferenceService.OpenComparisonWindow2(
                        fileNames.Item1,
                        fileNames.Item2,
                        caption,
                        tooltip,
                        leftLabel,
                        rightLabel,
                        string.Empty,
                        string.Empty,
                        (uint)options);
                }

                object docView;
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView);
                var diffViewer = ((IVsDifferenceCodeWindow)docView).DifferenceViewer;

                var session = ViewModel.Session;
                AddCompareBufferTag(diffViewer.LeftView.TextBuffer, session, fullPath, true);
                AddCompareBufferTag(diffViewer.RightView.TextBuffer, session, fullPath, false);
            }
            catch (Exception e)
            {
                ShowErrorInStatusBar("Error opening file", e);
            }
        }

        void AddCompareBufferTag(ITextBuffer buffer, IPullRequestSession session, string path, bool isLeftBuffer)
        {
            buffer.Properties.GetOrCreateSingletonProperty(
                typeof(PullRequestTextBufferInfo),
                () => new PullRequestTextBufferInfo(session, path, isLeftBuffer));
        }

        void ShowErrorInStatusBar(string message, Exception e)
        {
            var ns = Services.DefaultExportProvider.GetExportedValue<IStatusBarNotificationService>();
            ns?.ShowMessage(message + ": " + e.Message);
        }

        void FileListMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var file = (e.OriginalSource as FrameworkElement)?.DataContext as IPullRequestFileNode;

            if (file != null)
            {
                DoDiffFile(file).Forget();
            }
        }

        void FileListMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = (e.OriginalSource as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            
            if (item != null)
            {
                // Select tree view item on right click.
                item.IsSelected = true;
            }
        }

        void TreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            ApplyContextMenuBinding<TreeViewItem>(sender, e);
        }

        void ApplyContextMenuBinding<TItem>(object sender, ContextMenuEventArgs e) where TItem : Control
        {
            var container = (Control)sender;
            var item = (e.OriginalSource as Visual)?.GetSelfAndVisualAncestors().OfType<TItem>().FirstOrDefault();

            e.Handled = true;

            if (item != null)
            {
                var fileNode = item.DataContext as IPullRequestFileNode;

                if (fileNode != null)
                {
                    container.ContextMenu.DataContext = this.DataContext;

                    foreach (var menuItem in container.ContextMenu.Items.OfType<MenuItem>())
                    {
                        menuItem.CommandParameter = fileNode;
                    }

                    e.Handled = false;
                }
            }
        }

        private void ViewCommentsClick(object sender, RoutedEventArgs e)
        {
            var model = (object)ViewModel.Model;
            Services.Dte.Commands.Raise(
                GlobalCommands.CommandSetString,
                GlobalCommands.ShowPullRequestCommentsId,
                ref model,
                null);
        }

        private async void ViewFileCommentsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var file = (e.OriginalSource as Hyperlink)?.DataContext as IPullRequestFileNode;

                if (file != null)
                {
                    var param = (object)new InlineCommentNavigationParams
                    {
                        FromLine = -1,
                    };

                    await DoDiffFile(file);

                    // HACK: We need to wait here for the diff view to set itself up and move its cursor
                    // to the first changed line. There must be a better way of doing this.
                    await Task.Delay(1500);

                    Services.Dte.Commands.Raise(
                        GlobalCommands.CommandSetString,
                        GlobalCommands.NextInlineCommentId,
                        ref param,
                        null);
                }
            }
            catch { }
        }
    }
}
