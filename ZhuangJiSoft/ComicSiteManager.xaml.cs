using System.Windows;
using DeskApp.Models;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>漫画网站管理弹窗：增删改站点，改动即时写回 ComicViewModel。</summary>
public partial class ComicSiteManager : Wpf.Ui.Controls.FluentWindow
{
    private readonly ComicViewModel _vm;

    public ComicSiteManager(ComicViewModel vm)
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
        _vm = vm;
        DataContext = vm;
        SiteList.ItemsSource = vm.Sites;
    }

    private void AddSite_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ComicSiteDialog("添加漫画网站") { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _vm.AddOrUpdateSite(null, dlg.SiteName, dlg.SiteUrl);
        }
    }

    private void EditSite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComicSite site) return;
        var dlg = new ComicSiteDialog("编辑漫画网站", site.Name, site.Url) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _vm.AddOrUpdateSite(site, dlg.SiteName, dlg.SiteUrl);
        }
    }

    private void DeleteSite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ComicSite site) return;
        if (PromptDialog.Show(this, $"确定删除「{site.Name}」？", "删除网站",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.RemoveSite(site);
        }
    }

    private void ResetSites_Click(object sender, RoutedEventArgs e)
    {
        if (PromptDialog.Show(this, "恢复为内置默认网站列表？你添加/修改过的网站会被覆盖。", "恢复默认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _vm.ResetSites();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
