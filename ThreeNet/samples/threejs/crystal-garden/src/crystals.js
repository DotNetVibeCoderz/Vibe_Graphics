import * as THREE from 'three';

const CRYSTAL_COLORS = [0x4fd1e5, 0xb14eff, 0xff7a18, 0x30d158];

/**
 * Places glowing crystals on a ring around the centre piece.
 * @param {THREE.Scene} scene
 * @param {number} count
 * @returns {THREE.Mesh[]}
 */
export function createCrystals(scene, count) {
  const geometry = new THREE.OctahedronGeometry(0.4);
  const crystals = [];

  for (let i = 0; i < count; i++) {
    const color = CRYSTAL_COLORS[i % CRYSTAL_COLORS.length];
    const material = new THREE.MeshStandardMaterial({
      color,
      emissive: color,
      emissiveIntensity: 1.8,
      roughness: 0.2,
    });

    const crystal = new THREE.Mesh(geometry, material);
    const angle = (i / count) * Math.PI * 2;
    crystal.position.set(Math.cos(angle) * 5, 0.6, Math.sin(angle) * 5);
    crystal.scale.set(1, 1.8, 1);
    scene.add(crystal);
    crystals.push(crystal);
  }

  return crystals;
}
