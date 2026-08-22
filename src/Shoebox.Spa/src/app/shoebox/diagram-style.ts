/**
 * Mermaid's defaults draw every node as the same grey box, which throws away the
 * one thing this app is trying to teach: a queue is not a database is not a third
 * party, and a broken call is not like the others.
 *
 * So the shapes the cheatsheet documents get a look each, and the decoration is
 * derived from the diagram rather than typed by the user. Nobody pastes an
 * architecture and then hand-writes classDef lines.
 *
 * The rules this follows:
 *
 *   Teal is ours. Violet is somebody else's -- a third party is the one box you
 *   cannot go and fix, so it reads as outside. Magenta is only ever damage, which
 *   is why a broken edge is visible from across the room and nothing else is.
 *
 *   Decoration must never cost you a render. Every transform here is appended to
 *   the source, never rewritten into it, and the whole pass is guarded: if
 *   anything at all goes wrong the caller gets the original text back and mermaid
 *   draws the plain diagram. A styling bug must not look like a syntax error.
 */

/** Ids we have already classified, so the broad service pattern cannot re-claim them. */
type Roles = Map<string, string>;

const CLASS_DEFS = [
  // Ours: teal, solid, lit from inside.
  'classDef sbService fill:#13243a,stroke:#2fd4c4,stroke-width:2px,color:#e8f1f6',
  // A datastore is heavier than a service, so it sits darker and brighter-edged.
  'classDef sbStore fill:#0d1c2e,stroke:#5df2e2,stroke-width:2.5px,color:#e8f1f6',
  // A queue is a holding pen: dashed, because things sit in it rather than pass through.
  'classDef sbQueue fill:#111f31,stroke:#5df2e2,stroke-width:2px,stroke-dasharray:6 3,color:#e8f1f6',
  // A cache is fast and thin.
  'classDef sbCache fill:#132a36,stroke:#5df2e2,stroke-width:1.5px,color:#bff6ee',
  // Not ours. Violet, dashed, deliberately cooler than everything around it.
  'classDef sbExternal fill:#1a1730,stroke:#7c6cff,stroke-width:2px,stroke-dasharray:5 4,color:#d8d2ff',
  // Replicas: same teal, but a heavier edge to suggest a stack of them.
  'classDef sbReplica fill:#13243a,stroke:#2fd4c4,stroke-width:4px,color:#e8f1f6',
];

/** Longest-first: [[q]] and [(db)] must be claimed before the plain [svc] pattern runs. */
const SHAPES: ReadonlyArray<{ role: string; pattern: RegExp }> = [
  { role: 'sbQueue',    pattern: /\b([A-Za-z][\w-]*)\[\[[^\]]*\]\]/g },
  { role: 'sbStore',    pattern: /\b([A-Za-z][\w-]*)\[\([^)]*\)\]/g },
  { role: 'sbCache',    pattern: /\b([A-Za-z][\w-]*)\(\([^)]*\)\)/g },
  { role: 'sbExternal', pattern: /\b([A-Za-z][\w-]*)\{\{[^}]*\}\}/g },
  { role: 'sbService',  pattern: /\b([A-Za-z][\w-]*)\[[^\]]*\]/g },
];

/** `[Worker x5]` is a stack, not a box, and should not look like a single service. */
const REPLICA = /\b([A-Za-z][\w-]*)\[[^\]]*\bx\d+\s*\]/g;

/** Every arrow, in declaration order -- mermaid numbers linkStyle the same way. */
const ARROW = /(?:-{2,}>|-\.-+>|={2,}>|-{3,}|-{2,}[xo])/g;

function classify(diagram: string): Roles {
  const roles: Roles = new Map();
  for (const { role, pattern } of SHAPES) {
    for (const match of diagram.matchAll(pattern)) {
      const id = match[1];
      if (!roles.has(id)) roles.set(id, role);
    }
  }
  // Replicas win over plain service, because the stack is the interesting part.
  for (const match of diagram.matchAll(REPLICA)) {
    if (roles.get(match[1]) === 'sbService') roles.set(match[1], 'sbReplica');
  }
  return roles;
}

/**
 * Indices of the edges labelled broken. Mermaid numbers links in the order they
 * appear in the source, so a plain left-to-right scan is the same order.
 */
function brokenEdges(diagram: string): number[] {
  const broken: number[] = [];
  let index = 0;
  for (const match of diagram.matchAll(ARROW)) {
    const after = diagram.slice(match.index + match[0].length);
    const label = /^\s*\|([^|]*)\|/.exec(after);
    if (label && /^\s*broken\b/i.test(label[1])) broken.push(index);
    index += 1;
  }
  return broken;
}

/**
 * Someone who has written their own classDef has opinions, and this should not
 * fight them.
 */
function alreadyStyled(diagram: string): boolean {
  return /^\s*(classDef|linkStyle|style)\s/m.test(diagram);
}

export function decorate(diagram: string): string {
  try {
    if (alreadyStyled(diagram)) return diagram;

    const roles = classify(diagram);
    if (roles.size === 0) return diagram;

    const byRole = new Map<string, string[]>();
    for (const [id, role] of roles) {
      byRole.set(role, [...(byRole.get(role) ?? []), id]);
    }

    const lines = [...CLASS_DEFS];
    for (const [role, ids] of byRole) lines.push(`class ${ids.join(',')} ${role}`);

    // Magenta, thick, and dashed so it reads as damage even in a screenshot with
    // the colour washed out.
    for (const index of brokenEdges(diagram)) {
      lines.push(`linkStyle ${index} stroke:#ff2d6f,stroke-width:3px,stroke-dasharray:7 4`);
    }

    return `${diagram.replace(/\s+$/, '')}\n  ${lines.join('\n  ')}\n`;
  } catch {
    // See the header: a decoration bug must never present as a broken diagram.
    return diagram;
  }
}
