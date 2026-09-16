// Crystal Garden: a small but representative Three.js scene used to exercise
// the Three.js -> Three.Net converter (geometry, materials, textures, lights,
// groups, fog, tone mapping, orbit controls, animation loop and input).

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { createCrystals } from './src/crystals.js';

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0b0d12);
scene.fog = new THREE.FogExp2(0x0b0d12, 0.035);

const camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 0.1, 200);
camera.position.set(6, 4.5, 9);

const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.1;
renderer.shadowMap.enabled = true;
document.body.appendChild(renderer.domElement);

const controls = new OrbitControls(camera, renderer.domElement);
controls.target.set(0, 1, 0);
controls.enableDamping = true;

// Lights
const ambient = new THREE.AmbientLight(0x404a60, 0.6);
scene.add(ambient);

const sun = new THREE.DirectionalLight(0xfff1dd, 2.5);
sun.position.set(5, 10, 4);
sun.castShadow = true;
scene.add(sun);

const glow = new THREE.PointLight(0x4fd1e5, 30, 20);
glow.position.set(-3, 2.5, 2);
scene.add(glow);

// Ground with a tiled texture
const loader = new THREE.TextureLoader();
const checker = loader.load('textures/checker.png');
checker.wrapS = checker.wrapT = THREE.RepeatWrapping;
checker.repeat.set(8, 8);

const groundGeometry = new THREE.PlaneGeometry(40, 40);
const groundMaterial = new THREE.MeshStandardMaterial({ color: 0x8b93a7, roughness: 0.9, map: checker });
const ground = new THREE.Mesh(groundGeometry, groundMaterial);
ground.rotation.x = -Math.PI / 2;
ground.receiveShadow = true;
scene.add(ground);

// Centre piece
const knotGeometry = new THREE.TorusKnotGeometry(1, 0.32, 160, 24);
const knotMaterial = new THREE.MeshStandardMaterial({ color: 0xf0a030, metalness: 0.6, roughness: 0.25 });
const knot = new THREE.Mesh(knotGeometry, knotMaterial);
knot.position.set(0, 2, 0);
scene.add(knot);

// Orbiting moons inside a group
const moons = new THREE.Group();
moons.position.y = 2;
scene.add(moons);

const moonGeometry = new THREE.SphereGeometry(0.35, 32, 16);
const moonMaterial = new THREE.MeshPhongMaterial({ color: '#b14eff', shininess: 80 });
const moonA = new THREE.Mesh(moonGeometry, moonMaterial);
moonA.position.set(3, 0, 0);
moons.add(moonA);

const moonB = new THREE.Mesh(moonGeometry, new THREE.MeshBasicMaterial({ color: 0x30d158 }));
moonB.position.set(-3, 0.5, 0);
moons.add(moonB);

// Glass pedestal
const pedestal = new THREE.Mesh(
  new THREE.CylinderGeometry(1.4, 1.8, 0.6, 48),
  new THREE.MeshStandardMaterial({ color: 0xdde3ea, transparent: true, opacity: 0.55, roughness: 0.1 })
);
pedestal.position.y = 0.3;
scene.add(pedestal);

const crystals = createCrystals(scene, 10);

let paused = false;
window.addEventListener('keydown', (event) => {
  if (event.code === 'Space') {
    paused = !paused;
  }
});

window.addEventListener('resize', () => {
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
});

const clock = new THREE.Clock();

function animate() {
  requestAnimationFrame(animate);
  const t = clock.getElapsedTime();

  if (!paused) {
    knot.rotation.y += 0.01;
    knot.rotation.x += 0.004;
    moons.rotation.y += 0.02;
  }

  knot.position.y = 2 + Math.sin(t * 1.5) * 0.25;
  crystals.forEach((crystal, i) => {
    crystal.rotation.y = t * (0.5 + i * 0.05);
  });

  controls.update();
  renderer.render(scene, camera);
}

animate();
