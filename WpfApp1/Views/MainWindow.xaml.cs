using System.Windows;
using Microsoft.Win32;
using WpfApp1.ViewModels;

namespace WpfApp1.Views
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel ViewModel => DataContext as MainWindowViewModel;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = new MainWindowViewModel();
        }

        // '영상 선택' 버튼 클릭 시 파일 다이얼로그 처리
        private void SelectVideoButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            // 비디오 파일 필터 설정
            openFileDialog.Filter = "Video files (*.mp4;*.avi;*.mov)|*.mp4;*.avi;*.mov|All files (*.*)|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                if (ViewModel.SelectVideoCommand.CanExecute(openFileDialog.FileName))
                {
                    ViewModel.SelectVideoCommand.Execute(openFileDialog.FileName);
                }
            }
        }
    }
}