import json
import random
import re

def get_word_set(text):
    # Remove punctuation, lowercase, and split into words
    clean_text = re.sub(r'[^\w\s]', '', text.lower())
    words = clean_text.split()
    # Ignore short stop-words like 've', 'ile', 'bir'
    return set([w for w in words if len(w) > 3])

def is_false_negative_risk(original_title, random_title, comment):
    # 1. Check title-to-title similarity
    orig_words = get_word_set(original_title)
    rand_words = get_word_set(random_title)
    
    # If both titles are very short, require an exact match to fail
    if len(orig_words) == 0 or len(rand_words) == 0:
        return False
        
    # Calculate Jaccard similarity (Intersection over Union)
    intersection = orig_words.intersection(rand_words)
    union = orig_words.union(rand_words)
    title_similarity = len(intersection) / len(union)

    # If the titles share more than 25% of their core vocabulary, it's too risky
    if title_similarity > 0.25:
        return True

    # 2. Check random title against the comment body
    comment_words = get_word_set(comment)
    title_comment_intersection = rand_words.intersection(comment_words)
    
    # If the random title has 2 or more meaningful words that appear in the comment, skip it
    if len(title_comment_intersection) >= 2:
        return True

    return False

def generate_hard_negatives():
    with open("dataset.jsonl", "r", encoding="utf-8") as f:
        data = [json.loads(line) for line in f]

    all_titles = list(set([d["title"] for d in data]))

    hard_negatives = []
    random.seed(42)

    num_to_generate = min(1000, len(data))
    
    if num_to_generate > 0:
        sampled_positives = random.sample(data, num_to_generate)

        for pos in sampled_positives:
            original_title = pos["title"]
            comment = pos["detail"]
            
            # Find a safe random title that passes the false-negative check
            safe_match_found = False
            attempts = 0
            
            while not safe_match_found and attempts < 50:
                random_title = random.choice(all_titles)
                attempts += 1
                
                # Make sure it's strictly different and passes the risk check
                if random_title != original_title:
                    if not is_false_negative_risk(original_title, random_title, comment):
                        safe_match_found = True
            
            # If we couldn't find a safe title after 50 random attempts, skip this one
            if not safe_match_found:
                continue
                
            hard_negatives.append({
                "title": random_title,
                "detail": comment,
                "label": 0
            })

    augmented_dataset = data + hard_negatives
    random.shuffle(augmented_dataset)

    with open("dataset_augmented.jsonl", "w", encoding="utf-8") as f:
        for d in augmented_dataset:
            f.write(json.dumps(d, ensure_ascii=False) + "\n")

    print(f"Original dataset: {len(data)}")
    print(f"Generated Hard Negatives: {len(hard_negatives)}")
    print(f"Total entries for training: {len(augmented_dataset)}")

if __name__ == "__main__":
    generate_hard_negatives()
