# Star Wars Data

Extracted infobox data from the https://starwars.fandom.com/ API in json format using [pjmagee/starwars-data-extractor](https://github.com/pjmagee/starwars-data-extractor)

This repository contains various categories of data related to Star Wars by grouping and extracting page content based on pages that have an infobox template. The structure of the infobox is parsed 
in the most generic way possible and both text and links are stored for each property of the infobox.

## Docker

```sh
docker compose build # build the projects
docker compose up download # download the data
docker compose up process # process relationships
docker compose up database # start mongodb
docker compose up populate # populate mongodb
docker compose up api # serve api and swagger
```

## Data folder JSON Structure

```json
{
  "PageId": 4719,
  "PageUrl": "https://starwars.fandom.com/wiki/First_Battle_of_Kamino",
  "TemplateUrl": "https://starwars.fandom.com/wiki/Template:Battle",
  "ImageUrl": "https://static.wikia.nocookie.net/starwars/images/7/70/BattleofKamino2.jpg/revision/latest?cb=20150710040413",
  "Data": [
    {
      "Label": "Titles",
      "Links": [],
      "Values": [
        "First Battle of Kamino"
      ]
    },
    {
      "Label": "Conflict",
      "Links": [
        {
          "Content": "Clone Wars",
          "Href": "/wiki/Clone_Wars/Legends"
        }
      ],
      "Values": [
        "Clone Wars"
      ]
    },
    {
      "Label": "Date",
      "Links": [
        {
          "Content": "21.83 BBY",
          "Href": "/wiki/22_BBY/Legends"
        },
        {
          "Content": "Battle of Geonosis",
          "Href": "/wiki/First_Battle_of_Geonosis/Legends"
        }
      ],
      "Values": [
        "21.83 BBY, 2 months after the Battle of Geonosis"
      ]
    },
    {
      "Label": "Place",
      "Links": [
        {
          "Content": "Kamino",
          "Href": "/wiki/Kamino/Legends"
        }
      ],
      "Values": [
        "Kamino"
      ]
    },
    {
      "Label": "Outcome",
      "Links": [
        {
          "Content": "Republic",
          "Href": "/wiki/Galactic_Republic/Legends"
        }
      ],
      "Values": [
        "Republic victory"
      ]
    }
  ],
  "Relationships": [
    {
      "PageId": 43996,
      "PageUrl": "https://starwars.fandom.com/wiki/Shark_(starfighter)",
      "TemplateUrl": "/wiki/Template:Individual_ship"
    },
    {
      "PageId": 25913,
      "PageUrl": "https://starwars.fandom.com/wiki/Blue_Squadron_(Jedi_Order)",
      "Template": "https://starwars.fandom.com/wiki/Template:Military_unit"
    },
    {
      "PageId": 25917,
      "PageUrl": "https://starwars.fandom.com/wiki/Red_Squadron_(Galactic_Republic)%2fLegends",
      "Template": "https://starwars.fandom.com/wiki/Template:Military_unit"
    },
    {
      "PageId": 258,
      "PageUrl": "https://starwars.fandom.com/wiki/Clone_Wars%2fLegends",
      "Template": "https://starwars.fandom.com/wiki/Template:War"
    }
  ]
}
```
