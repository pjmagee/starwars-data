# Embeddings

Used for mongo atlas vector search, we vectorize the relevant fields and values and store them in the database in an embeddings field in each document. 
This field is stored as an array of floats, and is used to search for similar documents based on the cosine similarity of the vectors.

https://platform.openai.com/docs/guides/embeddings#embedding-models

We first download the data from the starwars wiki and store them locally as json documents.
Then we process relationships locally.

We can then upload the data into mongodb atlas, and then run the embeddings process to generate the embeddings for each document.

text-embedding-3-small 
