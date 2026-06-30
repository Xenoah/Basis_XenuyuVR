namespace Basis.Utilities
{
    public static class NameGenerator
    {
        public static string[] adjectives = { "Swift", "Brave", "Clever", "Fierce", "Nimble", "Silent", "Bold", "Lucky", "Strong", "Mighty", "Sneaky", "Fearless", "Wise", "Vicious", "Daring" };
        public static string[] nouns = { "Warrior", "Hunter", "Mage", "Rogue", "Paladin", "Shaman", "Knight", "Archer", "Monk", "Druid", "Assassin", "Sorcerer", "Ranger", "Guardian", "Berserker" };
        public static string[] titles = { "the Swift", "the Bold", "the Silent", "the Brave", "the Fierce", "the Wise", "the Protector", "the Shadow", "the Flame", "the Phantom" };
        // thread-safe な unique player name generation。
        public static string[] animals = { "Wolf", "Tiger", "Eagle", "Dragon", "Lion", "Bear", "Hawk", "Panther", "Raven", "Serpent", "Fox", "Falcon" };

        // Unity Rich Text 用の色名と対応する hex code。
        public static (string Name, string Hex)[] colors =
        {
            ("Red", "#FF0000"),
            ("Blue", "#0000FF"),
            ("Green", "#008000"),
            ("Yellow", "#FFFF00"),
            ("Black", "#000000"),
            ("White", "#FFFFFF"),
            ("Silver", "#C0C0C0"),
            ("Golden", "#FFD700"),
            ("Crimson", "#DC143C"),
            ("Azure", "#007FFF"),
            ("Emerald", "#50C878"),
            ("Amber", "#FFBF00")
        };
        public static string GenerateRandomPlayerName()
        {
            Random random = Random.Shared;

            // 各 array から 1 要素を random に選ぶ。
            string adjective = adjectives[random.Next(adjectives.Length)];
            string noun = nouns[random.Next(nouns.Length)];
            string title = titles[random.Next(titles.Length)];
            (string Name, string Hex) color = colors[random.Next(colors.Length)];
            string animal = animals[random.Next(animals.Length)];

            // 色部分に rich text を使って要素を組み合わせる。
            string colorText = $"<color={color.Hex}>{color.Name}</color>";
            string generatedName = $"{adjective}{noun} {title} of the {colorText} {animal}";

            // counter を追加できる形にして uniqueness を確保する。
            return $"{generatedName}";
        }
    }
}
