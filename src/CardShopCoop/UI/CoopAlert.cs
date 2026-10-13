namespace CardShopCoop.UI
{
    /// <summary>
    /// The on-screen alert for a network failure the player has to be told about - currently a
    /// reliable message whose handler threw and was therefore discarded. The core's dispatch loop
    /// (main thread) writes it; <see cref="CoopUI"/> owns the drawing and the dismiss button.
    ///
    /// It holds only the most recent failure and how many times that exact failure has repeated,
    /// so a handler that throws on every message cannot stack a pile of popups - the count just
    /// climbs.
    /// </summary>
    internal static class CoopAlert
    {
        private static string _title = "";
        private static string _detail = "";
        private static int _repeatCount;

        public static bool Active => _title.Length > 0;

        public static string Title => _title;

        public static string Detail => _detail;

        public static int RepeatCount => _repeatCount;

        /// <summary>Raises the alert, or bumps its count when the exact same failure repeats.</summary>
        public static void Show(string title, string detail)
        {
            title ??= "";
            detail ??= "";
            if (_title.Length > 0 && _title == title && _detail == detail)
            {
                _repeatCount++;
                return;
            }

            _title = title;
            _detail = detail;
            _repeatCount = 1;
        }

        public static void Dismiss()
        {
            _title = "";
            _detail = "";
            _repeatCount = 0;
        }
    }
}
