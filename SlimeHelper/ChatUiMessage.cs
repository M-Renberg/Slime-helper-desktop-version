using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace SlimeHelper
{
    public class ChatUiMessage : INotifyPropertyChanged
    {
        private string _text = "";
        public string Sender { get; set; } = "";

        public string Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Timestamp { get; set; } = DateTime.Now.ToString("HH:mm");
        public bool IsUser { get; set; }
        public bool IsSystem { get; set; }

        public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : (IsSystem ? HorizontalAlignment.Center : HorizontalAlignment.Left);

        public Brush BubbleBackground
        {
            get
            {
                if (IsUser) return new SolidColorBrush(Color.FromRgb(43, 84, 126)); // Soft slate blue
                if (IsSystem) return new SolidColorBrush(Color.FromRgb(32, 34, 42)); // Muted dark
                return new SolidColorBrush(Color.FromRgb(37, 40, 54)); // Soft card dark
            }
        }

        public Brush TextColor
        {
            get
            {
                if (IsSystem) return new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Muted gray
                return Brushes.White;
            }
        }

        public Thickness Margin => IsUser ? new Thickness(50, 4, 10, 4) : (IsSystem ? new Thickness(20, 6, 20, 6) : new Thickness(10, 4, 50, 4));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
