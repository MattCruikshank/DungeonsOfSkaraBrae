=== skeleton_aftermath ===
{ combat_won:
    You gather a few coins from the scattered bones and climb back toward the light. #color:green
    -> start
- else:
    { player_hp > 0:
        Wounded, you flee the crypt to lick your wounds. #color:yellow
        -> start
    - else:
        -> game_over
    }
}
