# sphere_jobs_test

The only scene is the sample scene. 

Most of the work happens in the JobController object/script - that's where the jobs run from, and the data for them is stored.

The properties can be changed from the inspector. 

The task asked for a left mouse click to spawn spheres (not sure if at specific location or not, i've left it entirely random within the bounds). That works, spawns random between 1-10 (can be changed in inspector). Right clicking despawns in a similar manner.

Besides those, you can also just directly set the desired_obj_count in the inspector, the previously mentioned mouse click just adds to this number.

Changing the desired size will add/decrease the spheres, making sure to retain previous data

Changing the bounds/transform/sphere radius will respawn all the spheres with the updated info

All of the jobs are burst compiled, so that should be turned on in the editor.


sources on other code/references i've used here:

https://allenchou.net/2020/01/dot-product-projection-reflection/ - sphere bounce on plane

https://www.habrador.com/tutorials/math/4-plane-ray-intersection/ - ray intersection with plane

https://github.com/Unity-Technologies/Megacity-Sample/tree/master/Assets/Scripts/Utils/KDTree - kdtree for sphere collisions with eachother

https://gist.github.com/mstevenson/5103365#file-fps-cs - fps counter (fps is limited to 60 in build)

There's a build in the "Demo" folder, for the purposes of making sure burst works. But most of the relevant features are more obvious in the editor
