interface ParsedSemver {
  core: [string, string, string];
  prerelease?: string[];
}

const numericIdentifier = "(?:0|[1-9]\\d*)";
const nonNumericIdentifier = "(?:[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)";
const prereleaseIdentifier = `(?:${numericIdentifier}|${nonNumericIdentifier})`;
const semverPattern = new RegExp(
  `^[vV]?(${numericIdentifier})\\.(${numericIdentifier})\\.(${numericIdentifier})` +
  `(?:-(${prereleaseIdentifier}(?:\\.${prereleaseIdentifier})*))?` +
  "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
);

function parseSemver(value: string): ParsedSemver | undefined {
  const match = semverPattern.exec(value.trim());
  if (!match) return undefined;

  return {
    core: [match[1], match[2], match[3]],
    prerelease: match[4]?.split("."),
  };
}

function compareNumericIdentifiers(left: string, right: string): number {
  if (left.length !== right.length) return left.length > right.length ? 1 : -1;
  return left === right ? 0 : left > right ? 1 : -1;
}

function comparePrereleaseIdentifiers(left: string, right: string): number {
  const leftNumeric = /^\d+$/.test(left);
  const rightNumeric = /^\d+$/.test(right);

  if (leftNumeric && rightNumeric) return compareNumericIdentifiers(left, right);
  if (leftNumeric !== rightNumeric) return leftNumeric ? -1 : 1;
  return left === right ? 0 : left > right ? 1 : -1;
}

export function compareSemver(left: string, right: string): number | undefined {
  const leftVersion = parseSemver(left);
  const rightVersion = parseSemver(right);
  if (!leftVersion || !rightVersion) return undefined;

  for (let index = 0; index < leftVersion.core.length; index += 1) {
    const comparison = compareNumericIdentifiers(
      leftVersion.core[index],
      rightVersion.core[index],
    );
    if (comparison !== 0) return comparison;
  }

  const leftPrerelease = leftVersion.prerelease;
  const rightPrerelease = rightVersion.prerelease;
  if (!leftPrerelease && !rightPrerelease) return 0;
  if (!leftPrerelease) return 1;
  if (!rightPrerelease) return -1;

  const length = Math.max(leftPrerelease.length, rightPrerelease.length);
  for (let index = 0; index < length; index += 1) {
    const leftIdentifier = leftPrerelease[index];
    const rightIdentifier = rightPrerelease[index];
    if (leftIdentifier === undefined) return -1;
    if (rightIdentifier === undefined) return 1;

    const comparison = comparePrereleaseIdentifiers(leftIdentifier, rightIdentifier);
    if (comparison !== 0) return comparison;
  }

  return 0;
}
