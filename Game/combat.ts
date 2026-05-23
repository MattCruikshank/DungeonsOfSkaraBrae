// Combat logic. Transpiled by esbuild and run in a V8 isolate on each fight.
// Edits hot-reload between fights (re-transpiled on every launch).
//
// Host API (defined by a prelude the C# host injects before this file):
declare function sendText(line: string): Promise<void>;
declare function sendChoices(choices: string[]): Promise<void>;
declare function readChoice(): Promise<number>;

interface CombatResult {
  won: boolean;
  playerHp: number;
}

const d = (n: number) => 1 + Math.floor(Math.random() * n);

async function combat(playerHp: number): Promise<CombatResult> {
  let skeletonHp = 12;
  let guarding = false;

  await sendText("\x1b[1;37mThe bones knit together into a clattering skeleton, rusted blade raised.\x1b[0m");

  while (skeletonHp > 0 && playerHp > 0) {
    await sendText(`\x1b[2mYou: ${playerHp} HP    Skeleton: ${skeletonHp} HP\x1b[0m`);
    await sendChoices(["Attack", "Defend", "Flee"]);
    const pick = await readChoice();

    if (pick === 0) {
      const dmg = 2 + d(6); // 3–8
      skeletonHp -= dmg;
      await sendText(`You swing — \x1b[33m${dmg}\x1b[0m damage.`);
      guarding = false;
    } else if (pick === 1) {
      guarding = true;
      await sendText("You raise your shield, bracing for the next blow.");
    } else if (pick === 2) {
      if (Math.random() < 0.5) {
        await sendText("\x1b[36mYou break away and flee into the dark.\x1b[0m");
        return { won: false, playerHp };
      }
      await sendText("You stumble — there's no escape!");
      guarding = false;
    } else {
      continue; // unknown input (e.g. disconnect); ignore
    }

    if (skeletonHp <= 0) break;

    let edmg = 1 + d(4); // 2–5
    if (guarding) edmg = Math.floor(edmg / 2);
    playerHp -= edmg;
    await sendText(`The skeleton strikes for \x1b[31m${edmg}\x1b[0m${guarding ? " \x1b[2m(blocked)\x1b[0m" : ""}.`);
  }

  const won = skeletonHp <= 0;
  await sendText(won
    ? "\x1b[1;32mThe skeleton collapses into a heap of bones.\x1b[0m"
    : "\x1b[1;31mYou fall to the cold crypt floor...\x1b[0m");
  return { won, playerHp };
}
