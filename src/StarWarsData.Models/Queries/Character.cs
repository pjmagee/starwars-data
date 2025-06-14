using MongoDB.Bson;

namespace StarWarsData.Models.Queries;

public class Character
{
    public string Name { get; set; }
    public string Born { get; set; }
    public string Died { get; set; }
    public float Years { get; set; }

    public static Character? Map(BsonDocument document)
    {
        try
        {
            var name = document["Data"]
                .AsBsonArray.First(i => i["Label"] == "Titles")["Values"][0]
                .ToString();
            var born = document["Data"]
                .AsBsonArray.First(i => i["Label"] == "Born")["Links"][0]["Content"]
                .ToString();
            var died = document["Data"]
                .AsBsonArray.First(i => i["Label"] == "Died")["Links"][0]["Content"]
                .ToString();
            var years = 0f;

            if (born.Contains("BBY") && died.Contains("ABY"))
            {
                years = Math.Abs(float.Parse(died.Split(' ')[0]) + float.Parse(born.Split(' ')[0]));
            }
            else
            {
                years = Math.Abs(float.Parse(died.Split(' ')[0]) - float.Parse(born.Split(' ')[0]));
            }

            return new Character
            {
                Name = name,
                Born = born,
                Died = died,
                Years = years,
            };
        }
        catch
        {
            // Yuk
            return null;
        }
    }
}
