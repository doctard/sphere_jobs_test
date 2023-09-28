# sphere_jobs_test

The only scene is the sample scene. 

Most of the work happens in the JobController object/script - that's where the jobs run from, and the data for them is stored.

The properties can be changed from the inspector. 

The task asked for a left mouse click to spawn spheres (not sure if at specific location or not, i've left it entirely random within the bounds). That works, spawns random between 1-10 (can be changed in inspector).

Besides those, you can also just directly set the desired_obj_count in the inspector, the previously mentioned mouse click just adds to this number.

Changing the desired size will add/decrease the spheres, making sure to retain previous data

Changing the bounds/transform/sphere radius will respawn all the balls with the updated info
