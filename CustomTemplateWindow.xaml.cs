using System.Windows;

namespace WpfMapApp1
{
    public partial class CustomTemplateWindow : Window
    {
        public string TemplateName { get; private set; } = string.Empty;

        public CustomTemplateWindow()
        {
            InitializeComponent();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            // Retrieve the entered template name.
            TemplateName = txtTemplateName.Text.Trim();

            // Optionally, validate the input (e.g., non-empty).
            if (string.IsNullOrWhiteSpace(TemplateName))
            {
                MessageBox.Show("Please enter a valid template name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
