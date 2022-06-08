using MongoDB.Bson;

namespace StarWarsData.Models.Queries;

public class War
{
    public string Name { get; set; }
    public string Beginning { get; set; }
    public string End { get; set; }
    public double Years { get; set; }

    public int Battles { get; set; } = 0;
    
    public static War? Map(BsonDocument document)
    {
        try
        {
            var name = document["Data"].AsBsonArray.First(i => i["Label"] == "Titles")["Values"][0].ToString();
            var start = document["Data"].AsBsonArray.First(i => i["Label"] == "Beginning")["Links"][0]["Content"].ToString();
            var end = document["Data"].AsBsonArray.First(i => i["Label"] == "End")["Links"][0]["Content"].ToString();
            var years = 0d;

            if (start.Contains("BBY") && end.Contains("ABY"))
            {
                years = Math.Abs(double.Parse(end.Split(' ')[0]) + double.Parse(start.Split(' ')[0]));
            }
            else
            {
                years = Math.Abs(double.Parse(end.Split(' ')[0]) - double.Parse(start.Split(' ')[0]));
            }
            
            var majorBattlesData = document["Data"].AsBsonArray.FirstOrDefault(i => i["Label"].AsString.Contains("Major battles"));

            int battles = 0;
            
            if (majorBattlesData is not null)
            {
                battles = majorBattlesData.AsBsonValue["Values"].AsBsonArray.Count;
            }

            return new War { Name = name, Beginning = start, End = end, Years = years, Battles = battles };
        }
        catch
        {
            // Yuk
            return null;
        }
    }
}