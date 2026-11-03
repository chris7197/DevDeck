namespace chris7197.DevDeck.Core
{
    public static class PathHelper
    {
        public static bool SamePath(string a, string b)
        {
            bool aEmpty = string.IsNullOrWhiteSpace(a);
            bool bEmpty = string.IsNullOrWhiteSpace(b);

            if (aEmpty && bEmpty)
                return true;

            if (aEmpty || bEmpty)
                return false;

            return
                string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
