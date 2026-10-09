using System;
using System.IO;
using System.Threading.Tasks;
using USM.Core.Services;

class Program {
    static async Task Main() {
        var detector = new GameDetector();
        string customSongsDir = detector.EnsureCustomSongsDir();
        Console.WriteLine($"PC Custom Songs Dir: {customSongsDir}");
        
        string[] queries = {
            "Bruno Mars Treasure",
            "Gorillaz Clint Eastwood",
            "Gorillaz New Gold",
            "Guns N' Roses Sweet Child o' Mine",
            "Guns N' Roses Welcome to the Jungle",
            "Mark Ronson Uptown Funk",
            "Melodicka Brothers Paradise City",
            "Nirvana Come As You Are",
            "Nirvana Smells Like Teen Spirit",
            "Red Hot Chili Peppers Californication",
            "Red Hot Chili Peppers Can't Stop",
            "Red Hot Chili Peppers Snow ((Hey Oh))",
            "Slipknot Before I Forget",
            "Slipknot Duality",
            "Slipknot The Devil in I",
            "Tame Impala Dracula",
            "Tame Impala Let It Happen",
            "Tame Impala My Old Ways",
            "Tame Impala Yes I'm Changing"
        };
        
        foreach (var query in queries) {
            Console.WriteLine($"Searching for: {query}");
            var (total, charts) = await ChorusClient.SearchAsync(query);
            if (charts.Count > 0) {
                var chart = charts[0];
                Console.WriteLine($"Downloading: {chart.Title} by {chart.Artist}");
                string tempSng = Path.Combine(Path.GetTempPath(), chart.Id + ".sng");
                
                await ChorusClient.DownloadFileAsync(chart.DownloadUrl, tempSng, null);
                Console.WriteLine("Converting to VR...");
                bool success = await SongConverter.ConvertSongAsync(tempSng, customSongsDir, null);
                Console.WriteLine(success ? "Installed!" : "Failed!");
            } else {
                Console.WriteLine("Not found!");
            }
        }
    }
}
